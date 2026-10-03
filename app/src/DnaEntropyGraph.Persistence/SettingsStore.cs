using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// <c>settings.json</c> (docs/architecture.md section 6; Appendix A section
/// 3's own line: "settings.json (System.Text.Json, atomic write)").
/// Deliberately not SQLite: this is small key/value app state a user might
/// hand-edit or delete to reset, and per-key flat values are the simplest
/// shape that a support engineer can open in Notepad.
///
/// DECISION (agent-made, reversible): the interface stays <c>GetString</c> /
/// <c>SetString</c> rather than growing typed accessors. Every setting so far
/// is naturally a string.
///
/// Issue #558 (data loss), the rules this class now keeps:
/// 1. The file is held as a <see cref="JsonObject"/>, not a
///    <c>Dictionary&lt;string,string&gt;</c>. A hand-edited number, bool, null,
///    object or array no longer makes the whole file "unreadable", and a
///    <c>SetString</c> rewrites every OTHER key with its original JSON value
///    and type (a number stays a number; we do not coerce on rewrite).
///    <c>GetString</c> reads a number or bool as its invariant JSON text
///    (<c>1</c>, <c>1.5</c>, <c>true</c>), an object or array as its raw JSON,
///    and null as null.
/// 2. A file that genuinely does not parse is NEVER rewritten in place. Before
///    the first write it is copied beside the original as
///    <c>settings.json.unreadable-yyyyMMdd-HHmmss</c> (a numeric suffix is
///    added if that name is taken), and only then replaced. If the copy fails
///    the write throws and the original is untouched: refusing the write is
///    the option that cannot lose data. A read alone never touches the file.
/// 3. An unparseable file is salvaged by a lenient scan for
///    <c>"key": "string"</c> / number / bool pairs. Reads see the salvaged
///    values and the fresh file after recovery carries them, so
///    <c>installation_id</c> survives a torn write and
///    <c>InstallationId.GetOrCreate</c> does not mint a new id (a new id makes
///    every labelled cloud resource invisible, Hard Rules 9 and 10).
/// 4. A file that exists but cannot be READ (locked, access denied) is not the
///    same as an unparseable one: reads degrade to empty, and every write
///    throws instead of replacing what we could not see.
/// 5. Writes are temp file + <c>File.Move(overwrite)</c>, so a crash leaves
///    the old file or the new one, never a half-written one.
///
/// <see cref="RecoveredFromUnreadableFile"/> is the hook for the recovery UX,
/// which waits on DECISION #404 (no UI or resw copy here). This project has no
/// logger dependency in Persistence; the event is exposed as the flag and the
/// kept file, never as file content.
/// </summary>
public sealed partial class SettingsStore : ISettingsStore
{
    private readonly string _filePath;
    private readonly object _gate = new();

    // Deliberately does nothing to disk - see SqliteDatabase's constructor
    // doc comment for why: a DI-graph guard test that constructs every
    // registered service must not have this one create a real directory
    // under %LOCALAPPDATA% as a side effect of a test that never calls
    // GetString/SetString.
    public SettingsStore(string filePath) => _filePath = filePath;

    /// <summary>The default per-user location, per docs/architecture.md section 6.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DNAEntropyGraph",
        "settings.json");

    /// <summary>
    /// True once a settings.json that did not parse (or could not be read) was
    /// seen by this instance. The original is kept as
    /// <c>settings.json.unreadable-*</c> before any write; nothing is deleted.
    /// </summary>
    public bool RecoveredFromUnreadableFile { get; private set; }

    public string? GetString(string key)
    {
        lock (_gate)
        {
            var state = ReadNoLock();
            return state.Values.TryGetPropertyValue(key, out var node) ? ToText(node) : null;
        }
    }

    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            var state = ReadNoLock();
            if (state.Unreadable is not null)
            {
                throw state.Unreadable;
            }

            if (state.ParseFailed)
            {
                KeepUnreadableCopyNoLock();
            }

            state.Values[key] = JsonValue.Create(value);
            WriteNoLock(state.Values);
        }
    }

    private static string? ToText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    private ReadState ReadNoLock()
    {
        if (!File.Exists(_filePath))
        {
            return new ReadState(new JsonObject(), ParseFailed: false, Unreadable: null);
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecoveredFromUnreadableFile = true;
            return new ReadState(new JsonObject(), ParseFailed: false, Unreadable: new IOException("settings.json could not be read; it was left untouched.", ex));
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject parsed)
            {
                return new ReadState(parsed, ParseFailed: false, Unreadable: null);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Falls through to salvage below.
        }

        RecoveredFromUnreadableFile = true;
        return new ReadState(Salvage(json), ParseFailed: true, Unreadable: null);
    }

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*(?:\"((?:[^\"\\\\]|\\\\.)*)\"|(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?|true|false))")]
    private static partial Regex PairPattern();

    private static JsonObject Salvage(string json)
    {
        var result = new JsonObject();
        foreach (Match m in PairPattern().Matches(json))
        {
            var key = Unescape(m.Groups[1].Value);
            if (key is null)
            {
                continue;
            }

            var text = m.Groups[2].Success ? Unescape(m.Groups[2].Value) : m.Groups[3].Value;
            if (text is not null)
            {
                result[key] = JsonValue.Create(text);
            }
        }

        return result;
    }

    private static string? Unescape(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + raw + "\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void KeepUnreadableCopyNoLock()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{_filePath}.unreadable-{stamp}";
        for (var n = 2; File.Exists(target); n++)
        {
            target = $"{_filePath}.unreadable-{stamp}-{n}";
        }

        File.Copy(_filePath, target, overwrite: false);
    }

    private void WriteNoLock(JsonObject values)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_filePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tempPath, values.ToJsonString());
            // Atomic on the same NTFS volume: a crash mid-write leaves either
            // the old file or the new one intact, never a half-written one.
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // Best effort; the original error is the one that matters.
            }

            throw;
        }
    }

    private sealed record ReadState(JsonObject Values, bool ParseFailed, IOException? Unreadable);
}
