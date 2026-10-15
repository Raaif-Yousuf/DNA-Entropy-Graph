using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
///    <c>installation_id</c>, published by a flushed temp file moved into place with
///    no overwrite (never torn; the loser of a race reads the winner's file), so a
///    settings.json failure can never change it (a new id orphans every labelled
///    cloud resource, Hard Rules 9 and 10). Absent, empty, whitespace-only or
///    BOM-only file: migrate a complete valid string id from settings.json (parse,
///    salvage, or a lenient scan of the raw text for a complete valid
///    <c>"installation_id"</c> pair after the corruption point), replacing a blank
///    file by an overwrite move under the mutex. If the raw text mentions the id
///    and none is recoverable, the text is kept aside and
///    <see cref="InstallationIdUnusableException"/> is raised; the caller mints only
///    when the text genuinely has no id. A non-empty file with an invalid id is kept
///    aside (<c>installation_id.invalid-&lt;stamp&gt;</c>) with the same exception;
///    it is never overwritten and never replaced. The format is not checked more
///    strictly than the label rule: legacy ids migrate, and atomic publish makes a
///    torn minted id impossible.
/// 4. Every IO, ACL or lock failure (temp file, folder, keep-aside copy, mutex,
///    move) surfaces as <see cref="SettingsUnavailableException"/> with the cause
///    kept, nothing changed and the recovered flag untouched. A missing file (even
///    right after an exists check) is simply absent.
/// 5. Writes go to a temp file via FileStream + Flush(true), then
///    File.Move(overwrite): a crash leaves the old file or the new one,
///    durably, never a half-written one.
/// 6. Every read-modify-write holds a named Mutex (<c>Local\</c>, hash of the
///    full path), so two instances or two processes never drop each other's
///    keys. Reads take no mutex: they only ever see a complete file.
/// 7. The callers are on the UI thread, so one public call spends at most
///    <see cref="WaitBudget"/> (about 300 ms) across the lock wait and the retries
///    (5 attempts, 40 ms apart), then gives up with the typed exception.
///
/// No logger in Persistence: the event is the flag, the kept files and the
/// typed exceptions, never file content.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    internal const int ReadAttemptLimit = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    // A complete "installation_id": "<value>" pair anywhere in the raw text (lenient scan for a corrupt settings.json).
    private static readonly Regex IdMention = new("\"installation_id\"\\s*:\\s*\"(?<id>[^\"\\\\\r\n]*)\"", RegexOptions.Compiled);

    private readonly string _filePath;
    private readonly string _idPath;
    private readonly string _mutexName;
    private readonly object _gate = new();
    private DateTime _deadlineUtc;

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

    /// <summary>True once this instance saw a settings.json that did not parse. Sticky; see class remarks.</summary>
    public bool RecoveredFromUnreadableFile { get; private set; }

    /// <summary>Attempts the most recent file read took (test hook for the bounded retry).</summary>
    internal int LastReadAttempts { get; private set; }

    /// <summary>Test seam: called with an operation name just before each file-system or lock step, so a test can inject an IO or ACL fault.</summary>
    internal Action<string>? FaultHook { get; set; }

    /// <summary>The named mutex this instance locks on (test hook for the bounded wait).</summary>
    internal string MutexName => _mutexName;

    /// <summary>Total wait one public call may spend on locks and retries (see class remarks, rule 7).</summary>
    internal TimeSpan WaitBudget { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Test seam (#625): the clock the wait budget is measured on. A test supplies virtual time so the retry count never depends on machine load.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Test seam (#625): how the retry loop waits between attempts. Production sleeps the thread; a test advances its virtual <see cref="Clock"/> instead.</summary>
    internal Action<TimeSpan> Pause { get; set; } = Thread.Sleep;

    public string? GetString(string key)
    {
        lock (_gate)
        {
            return Guarded(() =>
            {
                if (key == InstallationId.SettingsKey)
                {
                    return GetInstallationIdNoLock();
                }

                var state = ReadSettingsNoLock();
                return state.Values.TryGetPropertyValue(key, out var node) ? ToText(node) : null;
            });
        }
    }

    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            Guarded<object?>(() =>
            {
                if (key == InstallationId.SettingsKey)
                {
                    SetInstallationIdNoLock(value);
                    return null;
                }

                using (AcquireMutex())
                {
                    var state = ReadSettingsNoLock();
                    if (state.ParseFailed)
                    {
                        KeepUnreadableCopyNoLock(state.Raw);
                    }

                    state.Values[key] = JsonValue.Create(value);
                    WriteSettingsNoLock(state.Values);
                }

                return null;
            });
        }
    }

    /// <summary>
    /// Starts this call's wait budget and turns every IO, ACL or lock failure into the one typed
    /// exception callers catch (inner exception kept). The two typed exceptions pass through.
    /// </summary>
    private T Guarded<T>(Func<T> action)
    {
        _deadlineUtc = Clock.GetUtcNow().UtcDateTime + WaitBudget;
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not (SettingsUnavailableException or InstallationIdUnusableException)
            && ex is IOException or UnauthorizedAccessException or SecurityException or WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            throw new SettingsUnavailableException("The settings could not be read or saved; nothing was changed.", ex);
        }
    }

    private TimeSpan Remaining
    {
        get
        {
            var left = _deadlineUtc - Clock.GetUtcNow().UtcDateTime;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    private void Fault(string operation) => FaultHook?.Invoke(operation);

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

            var state = ReadSettingsNoLock();
            var legacy = state.Values.TryGetPropertyValue(InstallationId.SettingsKey, out var node) ? node : null;
            if (legacy is JsonValue v && v.TryGetValue<string>(out var text) && InstallationId.IsValid(text))
            {
                WriteIdFileIfAbsent(text);
                return ReadIdFile() ?? text;
            }

            if (state.ParseFailed && state.Raw is { } raw)
            {
                // The id may sit after the corruption point, where the structured salvage never reaches.
                foreach (Match m in IdMention.Matches(raw))
                {
                    var candidate = m.Groups["id"].Value;
                    if (InstallationId.IsValid(candidate))
                    {
                        WriteIdFileIfAbsent(candidate);
                        return ReadIdFile() ?? candidate;
                    }
                }

                if (raw.Contains(InstallationId.SettingsKey, StringComparison.Ordinal))
                {
                    // An id was there and cannot be recovered: minting would orphan every labelled
                    // cloud resource (Hard Rules 9 and 10). Keep the file aside and surface it (#404).
                    KeepUnreadableCopyNoLock(raw);
                    throw new InstallationIdUnusableException(
                        "settings.json mentions an installation id that could not be recovered. A copy was kept beside it.");
                }
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
        var trimmed = raw?.Replace("\uFEFF", string.Empty, StringComparison.Ordinal).Trim();
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

        Fault("copy");
        File.Copy(_idPath, UniqueSibling(_idPath, ".invalid-"), overwrite: false);
    }

    /// <summary>
    /// Publishes the id by writing a flushed temp file and moving it into place with no overwrite, so a
    /// reader never sees a torn prefix and the loser of a race keeps the winner's file. The one overwrite
    /// is over an existing file that holds only whitespace or a BOM (a crashed or hand-touched file, never
    /// an id), decided under the mutex. Internal so a test can drive the loser arm directly.
    /// </summary>
    internal void WriteIdFileIfAbsent(string id)
    {
        EnsureDirectory(_idPath);
        var tempPath = $"{_idPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            WriteDurable(tempPath, id);
            Fault("id-before-move");
            var overwrite = ExistingIdFileIsBlank();
            try
            {
                File.Move(tempPath, _idPath, overwrite);
            }
            catch (IOException) when (File.Exists(_idPath))
            {
                // Lost the race (or still locked by the winner): the winner's file stands.
            }
        }
        finally
        {
            DeleteQuietly(tempPath);
        }
    }

    private bool ExistingIdFileIsBlank()
    {
        var raw = ReadTextWithRetry(_idPath);
        return raw is not null && string.IsNullOrWhiteSpace(raw.Replace("\uFEFF", string.Empty, StringComparison.Ordinal));
    }

    private void WriteDurable(string tempPath, string text)
    {
        Fault("temp-create");
        using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(new UTF8Encoding(false).GetBytes(text));
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            // Best effort; never mask the original exception.
        }
    }

    // ---- settings.json ----

    private ReadState ReadSettingsNoLock()
    {
        var json = ReadTextWithRetry(_filePath);
        if (json is null)
        {
            return new ReadState(new JsonObject(), ParseFailed: false, Raw: null);
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject parsed)
            {
                return new ReadState(parsed, ParseFailed: false, Raw: null);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Falls through to salvage below.
        }

        RecoveredFromUnreadableFile = true;
        return new ReadState(Salvage(json), ParseFailed: true, Raw: json);
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

    /// <summary>Copies the unreadable file aside unless an identical copy is already there (a repeated read must not litter).</summary>
    private void KeepUnreadableCopyNoLock(string? raw)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_filePath)) ?? string.Empty;
        foreach (var existing in Directory.GetFiles(dir, Path.GetFileName(_filePath) + ".unreadable-*"))
        {
            if (raw is not null && ReadTextWithRetry(existing) == raw)
            {
                return;
            }
        }

        Fault("copy");
        File.Copy(_filePath, UniqueSibling(_filePath, ".unreadable-"), overwrite: false);
    }

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
            WriteDurable(tempPath, values.ToJsonString());

            // Atomic on the same NTFS volume: the old file or the new one.
            Retry(() =>
            {
                Fault("move");
                File.Move(tempPath, _filePath, overwrite: true);
                return 0;
            });
        }
        catch
        {
            DeleteQuietly(tempPath);
            throw;
        }
    }

    private void EnsureDirectory(string filePath)
    {
        Fault("mkdir");
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

    private T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttemptLimit || Remaining == TimeSpan.Zero)
                {
                    throw new SettingsUnavailableException(
                        "The settings file is in use or not accessible; nothing was changed.", ex);
                }

                Pause(RetryDelay);
            }
        }
    }

    private MutexLease AcquireMutex()
    {
        Fault("mutex");
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            if (!mutex.WaitOne(Remaining))
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

    private sealed record ReadState(JsonObject Values, bool ParseFailed, string? Raw);
}
