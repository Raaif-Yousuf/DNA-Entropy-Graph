using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// <c>settings.json</c> (docs/architecture.md section 6) plus the write-once
/// <c>installation_id</c> file beside it. Deliberately not SQLite: this is
/// small key/value app state a user might hand-edit or delete to reset.
///
/// DECISION (agent-made, reversible): the interface stays <c>GetString</c> /
/// <c>SetString</c> rather than growing typed accessors.
///
/// Issue #558 (data loss), the rules this class keeps:
/// 1. The file is held as a <see cref="JsonObject"/>. A hand-edited number,
///    bool, null, object or array no longer makes the file "unreadable", and a
///    <c>SetString</c> rewrites every OTHER key with its original JSON type (no
///    coercion on rewrite). <c>GetString</c> reads a number or bool as its
///    invariant JSON text, an object or array as raw JSON, null as null.
/// 2. A file that genuinely does not parse is NEVER rewritten in place: before
///    the first write it is copied to <c>settings.json.unreadable-&lt;stamp&gt;</c>
///    (the write is refused if the copy fails), and the scalar depth-1 pairs
///    that completed before the error are salvaged with a
///    <see cref="Utf8JsonReader"/> (types preserved, nested values skipped
///    whole, a number cut off at end of input dropped as ambiguous).
///    <see cref="RecoveredFromUnreadableFile"/> is set only for this
///    corruption, and is sticky for the life of the instance: it is the hook
///    for the recovery UX (DECISION #404), which owns clearing it.
/// 3. The installation id is NOT in settings.json. It is a write-once file
///    <c>installation_id</c> (CreateNew + Flush(true)), so a settings.json
///    failure can never change it (a new id orphans every labelled cloud
///    resource, Hard Rules 9 and 10). Absent file: migrate a complete valid
///    string id from settings.json (parse or salvage), else the caller mints.
///    Two first-run processes converge: CreateNew has one winner and the loser
///    reads the winner's file. A non-empty file with an invalid id is kept
///    aside (<c>installation_id.invalid-&lt;stamp&gt;</c>) and
///    <see cref="InstallationIdUnusableException"/> is raised; it is never
///    overwritten and never replaced. An EMPTY file is a crashed first write
///    (the id is only handed out after the write returns, so nothing can carry
///    it) and counts as absent.
/// 4. A persistent lock or access error is retried 5 x 50 ms, then raises
///    <see cref="SettingsUnavailableException"/> with nothing changed and the
///    recovered flag untouched. A missing file (even right after an exists
///    check) is simply absent.
/// 5. Writes go to a temp file via FileStream + Flush(true), then
///    File.Move(overwrite): a crash leaves the old file or the new one,
///    durably, never a half-written one.
/// 6. Every read-modify-write holds a named Mutex (<c>Local\</c>, hash of the
///    full path), so two instances or two processes never drop each other's
///    keys. Reads take no mutex: they only ever see a complete file.
///
/// No logger in Persistence: the event is the flag, the kept files and the
/// typed exceptions, never file content.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    internal const int ReadAttemptLimit = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(10);

    private readonly string _filePath;
    private readonly string _idPath;
    private readonly string _mutexName;
    private readonly object _gate = new();

    // Deliberately does nothing to disk - see SqliteDatabase's constructor
    // doc comment for why: a DI-graph guard test that constructs every
    // registered service must not have this one create a real directory
    // under %LOCALAPPDATA% as a side effect of a test that never calls
    // GetString/SetString.
    public SettingsStore(string filePath)
    {
        _filePath = filePath;
        var full = Path.GetFullPath(filePath);
        _idPath = Path.Combine(Path.GetDirectoryName(full) ?? string.Empty, "installation_id");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
        _mutexName = $@"Local\DnaEntropyGraph.Settings.{hash}";
    }

    /// <summary>The default per-user location, per docs/architecture.md section 6.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DNAEntropyGraph",
        "settings.json");

    /// <summary>True once this instance saw a settings.json that did not parse. Sticky; see class remarks.</summary>
    public bool RecoveredFromUnreadableFile { get; private set; }

    /// <summary>Attempts the most recent file read took (test hook for the bounded retry).</summary>
    internal int LastReadAttempts { get; private set; }

    public string? GetString(string key)
    {
        lock (_gate)
        {
            if (key == InstallationId.SettingsKey)
            {
                return GetInstallationIdNoLock();
            }

            var state = ReadSettingsNoLock();
            return state.Values.TryGetPropertyValue(key, out var node) ? ToText(node) : null;
        }
    }

    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            if (key == InstallationId.SettingsKey)
            {
                SetInstallationIdNoLock(value);
                return;
            }

            using (AcquireMutex())
            {
                var state = ReadSettingsNoLock();
                if (state.ParseFailed)
                {
                    KeepUnreadableCopyNoLock();
                }

                state.Values[key] = JsonValue.Create(value);
                WriteSettingsNoLock(state.Values);
            }
        }
    }

    private static string? ToText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    // ---- installation id ----

    private string? GetInstallationIdNoLock()
    {
        var id = ReadIdFile();
        if (id is not null)
        {
            return id;
        }

        using (AcquireMutex())
        {
            // Re-check under the lock: another process may have just written it.
            id = ReadIdFile();
            if (id is not null)
            {
                return id;
            }

            var legacy = ReadSettingsNoLock().Values.TryGetPropertyValue(InstallationId.SettingsKey, out var node) ? node : null;
            if (legacy is JsonValue v && v.TryGetValue<string>(out var text) && InstallationId.IsValid(text))
            {
                WriteIdFileIfAbsent(text);
                return ReadIdFile() ?? text;
            }

            return null;
        }
    }

    private void SetInstallationIdNoLock(string value)
    {
        if (!InstallationId.IsValid(value))
        {
            throw new ArgumentException("Not a valid installation id.", nameof(value));
        }

        using (AcquireMutex())
        {
            // Write-once: an existing file (valid or not) is never replaced.
            if (ReadIdFile() is null)
            {
                WriteIdFileIfAbsent(value);
                if (ReadIdFile() is null)
                {
                    throw new SettingsUnavailableException("The installation id could not be saved; nothing was changed.");
                }
            }
        }
    }

    /// <summary>The valid id, or null when the file is absent or empty. Throws on a non-empty invalid one.</summary>
    private string? ReadIdFile()
    {
        var raw = ReadTextWithRetry(_idPath);
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (InstallationId.IsValid(trimmed))
        {
            return trimmed;
        }

        KeepInvalidIdCopy(raw!);
        throw new InstallationIdUnusableException(
            "The installation id file does not hold a valid id. It was left untouched and a copy was kept beside it.");
    }

    private void KeepInvalidIdCopy(string raw)
    {
        var dir = Path.GetDirectoryName(_idPath) ?? string.Empty;
        foreach (var existing in Directory.GetFiles(dir, "installation_id.invalid-*"))
        {
            if (ReadTextWithRetry(existing) == raw)
            {
                return;
            }
        }

        File.Copy(_idPath, UniqueSibling(_idPath, ".invalid-"), overwrite: false);
    }

    /// <summary>Internal so a test can drive the CreateNew arm directly: under the mutex the callers never reach it with a file present.</summary>
    internal void WriteIdFileIfAbsent(string id)
    {
        EnsureDirectory(_idPath);
        var bytes = new UTF8Encoding(false).GetBytes(id);

        // An empty file is a crashed first write: take it over. Anything else
        // that exists makes CreateNew fail and the existing file wins.
        var mode = File.Exists(_idPath) && new FileInfo(_idPath).Length == 0 ? FileMode.Truncate : FileMode.CreateNew;
        try
        {
            using var stream = new FileStream(_idPath, mode, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(_idPath))
        {
            // Lost the race (or still locked by the winner): the winner's file stands.
        }
    }

    // ---- settings.json ----

    private ReadState ReadSettingsNoLock()
    {
        var json = ReadTextWithRetry(_filePath);
        if (json is null)
        {
            return new ReadState(new JsonObject(), ParseFailed: false);
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject parsed)
            {
                return new ReadState(parsed, ParseFailed: false);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Falls through to salvage below.
        }

        RecoveredFromUnreadableFile = true;
        return new ReadState(Salvage(json), ParseFailed: true);
    }

    /// <summary>
    /// Depth-1 properties whose scalar value completed before the parser
    /// failed, types preserved. Nested values are skipped whole. A number cut
    /// off at end of input is never emitted by the reader (see below); a
    /// complete string, bool or null is kept.
    /// </summary>
    internal static JsonObject Salvage(string json)
    {
        var result = new JsonObject();
        var bytes = new UTF8Encoding(false).GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        string? pendingName = null;

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return result;
            }

            while (reader.Read())
            {
                if (reader.CurrentDepth == 0)
                {
                    return result; // closing brace: nothing was broken after all
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pendingName = reader.GetString();
                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        reader.Skip();
                        pendingName = null;
                        break;
                    case JsonTokenType.String when pendingName is not null:
                        result[pendingName] = JsonValue.Create(reader.GetString());
                        pendingName = null;
                        break;
                    case JsonTokenType.Number when pendingName is not null:
                        result[pendingName] = JsonNode.Parse(Encoding.UTF8.GetString(reader.ValueSpan));
                        pendingName = null;
                        break;
                    case JsonTokenType.True or JsonTokenType.False when pendingName is not null:
                        result[pendingName] = JsonValue.Create(reader.TokenType == JsonTokenType.True);
                        pendingName = null;
                        break;
                    case JsonTokenType.Null when pendingName is not null:
                        result[pendingName] = null;
                        pendingName = null;
                        break;
                    default:
                        pendingName = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Stop at the first error; everything completed before it is kept.
            // MEASURED 2026-10-03: a number cut off at end of input inside an object
            // ("hours":12 from 123) makes Utf8JsonReader throw on that number itself, so
            // it is never emitted and cannot be mistaken for a complete value
            // (pinned by A_number_cut_off_at_end_of_input_is_dropped...).
        }

        return result;
    }

    private void KeepUnreadableCopyNoLock() => File.Copy(_filePath, UniqueSibling(_filePath, ".unreadable-"), overwrite: false);

    private static string UniqueSibling(string path, string infix)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{path}{infix}{stamp}";
        for (var n = 2; File.Exists(target); n++)
        {
            target = $"{path}{infix}{stamp}-{n}";
        }

        return target;
    }

    private void WriteSettingsNoLock(JsonObject values)
    {
        EnsureDirectory(_filePath);
        var tempPath = $"{_filePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(values.ToJsonString()));
                stream.Flush(flushToDisk: true);
            }

            // Atomic on the same NTFS volume: the old file or the new one.
            Retry(() =>
            {
                File.Move(tempPath, _filePath, overwrite: true);
                return 0;
            });
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort; never mask the original exception.
            }

            throw;
        }
    }

    private static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    // ---- IO helpers ----

    /// <summary>File text, or null when the file does not exist. Retries a sharing violation, then raises the typed exception.</summary>
    private string? ReadTextWithRetry(string path)
    {
        var attempts = 0;
        try
        {
            return Retry(() =>
            {
                attempts++;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    return reader.ReadToEnd();
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return null;
                }
            });
        }
        finally
        {
            LastReadAttempts = attempts;
        }
    }

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttemptLimit)
                {
                    throw new SettingsUnavailableException(
                        "The settings file is in use or not accessible; nothing was changed.", ex);
                }

                Thread.Sleep(RetryDelay);
            }
        }
    }

    private MutexLease AcquireMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            if (!mutex.WaitOne(MutexTimeout))
            {
                mutex.Dispose();
                throw new SettingsUnavailableException("The settings file is busy in another window; nothing was changed.");
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died holding it. We own it now; the files are
            // only ever replaced atomically, so they are consistent.
        }

        return new MutexLease(mutex);
    }

    private readonly struct MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private sealed record ReadState(JsonObject Values, bool ParseFailed);
}
