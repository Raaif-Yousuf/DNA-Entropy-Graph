using System.Text.Json;
using System.Text.Json.Serialization;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// <c>settings.json</c> (docs/architecture.md section 6; Appendix A section
/// 3's own line: "settings.json (System.Text.Json source-gen, atomic
/// write)"). Deliberately not SQLite: this is small key/value app state a
/// user might hand-edit or delete to reset, and per-key flat strings are the
/// simplest shape that a support engineer can open in Notepad.
///
/// DECISION (agent-made, reversible): the interface stays <c>GetString</c> /
/// <c>SetString</c> rather than growing typed accessors tonight. Every
/// setting so far (Theme, default output folder) is naturally a string, and
/// a flat <c>{"Theme":"Dark"}</c> file is not the "one JSON blob nobody can
/// query" shape the wave brief warned about - that shape is a *single* key
/// holding a serialized object. The one thing this decision does not yet
/// answer is per-project vs per-installation scope (docs/architecture.md
/// only ever describes one <c>settings.json</c> per Windows user); issue
/// #67 marks that out of scope ("Anything not named above"), so it is
/// recorded here rather than solved.
/// </summary>
public sealed class SettingsStore : ISettingsStore
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
    /// True the moment a corrupt/unreadable settings.json was found and
    /// silently replaced with an empty one (same call as SqliteDatabase's
    /// corrupt-file handling: losing UI prefs is a much smaller bug than the
    /// app refusing to launch over a half-written file from a force
    /// restart).
    /// </summary>
    public bool RecoveredFromUnreadableFile { get; private set; }

    public string? GetString(string key)
    {
        lock (_gate)
        {
            var values = ReadAllNoLock();
            return values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            var values = ReadAllNoLock();
            values[key] = value;
            WriteAllNoLock(values);
        }
    }

    private Dictionary<string, string> ReadAllNoLock()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var values = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DictionaryStringString);
            return values ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            RecoveredFromUnreadableFile = true;
            return new Dictionary<string, string>();
        }
        catch (IOException)
        {
            RecoveredFromUnreadableFile = true;
            return new Dictionary<string, string>();
        }
    }

    private void WriteAllNoLock(Dictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(values, SettingsJsonContext.Default.DictionaryStringString);
        var tempPath = $"{_filePath}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, json);
        // Atomic on the same NTFS volume (Blobstore uses the identical
        // temp+rename pattern on the worker side - docs/job_contract.md /
        // Appendix B's LocalBlobstore): a crash mid-write leaves either the
        // old file or the new one intact, never a half-written one.
        File.Move(tempPath, _filePath, overwrite: true);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
