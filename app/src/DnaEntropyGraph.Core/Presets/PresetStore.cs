using System.Text.Json;
using System.Text.Json.Serialization;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Presets;

/// <summary>Why <see cref="PresetStore.Save"/> did or did not keep a preset.</summary>
public enum PresetSaveResult
{
    Saved,
    EmptyName,
    NameTaken,
}

/// <summary>The outcome of <see cref="PresetStore.Save"/>: the result, and the preset when it was kept.</summary>
public readonly record struct PresetSaveOutcome(PresetSaveResult Result, RunPreset? Preset);

/// <summary>The outcome of <see cref="PresetStore.Import"/>: how many presets were added, and whether the text was unusable or had unusable entries.</summary>
public readonly record struct PresetImportResult(int Imported, int Skipped, bool Problem);

/// <summary>
/// The user's own presets, kept in <c>settings.json</c> under one key as JSON and exportable as the same JSON (issue #136).
/// Output files are stored as the worker's names, and the options as their wire words, so a reordered or grown enum cannot
/// change what a saved preset means. The built-ins are code, never stored, so an app update can improve them.
/// </summary>
public sealed class PresetStore
{
    /// <summary>The settings key the user's presets are kept under.</summary>
    public const string SettingsKey = "user_presets";

    private const int MaxNameLength = 60;
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly ISettingsStore _settings;

    public PresetStore(ISettingsStore settings) => _settings = settings;

    /// <summary>The built-ins first, then the user's own in the order they were saved.</summary>
    public IReadOnlyList<RunPreset> List() => [.. BuiltInPresets.All, .. ReadUser()];

    /// <summary>
    /// Keeps a preset under <paramref name="name"/>. A name already used by a built-in is refused; a name already used by one of
    /// the user's own replaces it in place (names are compared ignoring case). An empty output choice throws: a run must return a file.
    /// </summary>
    public PresetSaveOutcome Save(string name, Direction direction, int contextLength, Topology topology, OutputFileKinds outputs)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return new PresetSaveOutcome(PresetSaveResult.EmptyName, null);
        }

        clean = clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
        var normalized = OutputCatalog.Normalize(outputs & OutputCatalog.Everything);
        if (normalized == OutputFileKinds.None)
        {
            throw new ArgumentException("Choose at least one output file.", nameof(outputs));
        }

        if (BuiltInPresets.All.Any(p => SameName(p.Name, clean)))
        {
            return new PresetSaveOutcome(PresetSaveResult.NameTaken, null);
        }

        var user = ReadUser();
        var existing = user.FindIndex(p => SameName(p.Name, clean));
        var preset = new RunPreset(existing >= 0 ? user[existing].Id : "u-" + Guid.NewGuid().ToString("N")[..12], clean, false, direction, contextLength, topology, normalized);
        if (existing >= 0)
        {
            user[existing] = preset;
        }
        else
        {
            user.Add(preset);
        }

        WriteUser(user);
        return new PresetSaveOutcome(PresetSaveResult.Saved, preset);
    }

    /// <summary>Deletes one of the user's presets. False for a built-in or an id that is not there.</summary>
    public bool Delete(string id)
    {
        var user = ReadUser();
        if (user.RemoveAll(p => p.Id == id) == 0)
        {
            return false;
        }

        WriteUser(user);
        return true;
    }

    /// <summary>The user's own presets as JSON text (built-ins are not exported: every install has them).</summary>
    public string ExportJson() => JsonSerializer.Serialize(new PresetFile { Schema = SchemaVersion, Presets = ReadUser().Select(PresetDto.From).ToList() }, Json);

    /// <summary>
    /// Adds the presets in <paramref name="json"/> to the user's own. An entry with an empty name, an unknown word or no output file is
    /// skipped; a name already used by a built-in is skipped; a name already used by one of the user's own is replaced. Nothing is
    /// added when the text as a whole cannot be read.
    /// </summary>
    public PresetImportResult Import(string? json)
    {
        PresetFile? file;
        try
        {
            file = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<PresetFile>(json, Json);
        }
        catch (JsonException)
        {
            return new PresetImportResult(0, 0, true);
        }

        if (file?.Presets is null || file.Schema != SchemaVersion)
        {
            return new PresetImportResult(0, 0, true);
        }

        var imported = 0;
        var skipped = 0;
        var user = ReadUser();
        var changed = false;
        foreach (var dto in file.Presets)
        {
            var preset = dto?.ToPreset();
            if (preset is null || preset.OutputFiles == OutputFileKinds.None || BuiltInPresets.All.Any(p => SameName(p.Name, preset.Name)))
            {
                skipped++;
                continue;
            }

            var existing = user.FindIndex(p => SameName(p.Name, preset.Name));
            if (existing >= 0)
            {
                user[existing] = preset with { Id = user[existing].Id };
            }
            else
            {
                user.Add(preset with { Id = "u-" + Guid.NewGuid().ToString("N")[..12] });
            }

            imported++;
            changed = true;
        }

        if (changed)
        {
            WriteUser(user);
        }

        return new PresetImportResult(imported, skipped, imported == 0);
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private List<RunPreset> ReadUser()
    {
        var stored = _settings.GetString(SettingsKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<PresetFile>(stored, Json);
            return (file?.Presets ?? []).Select(d => d?.ToPreset()).OfType<RunPreset>().Where(p => p.OutputFiles != OutputFileKinds.None).ToList();
        }
        catch (JsonException)
        {
            // An unreadable value reads as no presets; the next save replaces it.
            return [];
        }
    }

    private void WriteUser(List<RunPreset> user)
        => _settings.SetString(SettingsKey, JsonSerializer.Serialize(new PresetFile { Schema = SchemaVersion, Presets = user.Select(PresetDto.From).ToList() }, Json));

    private sealed class PresetFile
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; }

        [JsonPropertyName("presets")]
        public List<PresetDto>? Presets { get; set; }
    }

    private sealed class PresetDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("direction")]
        public string? Direction { get; set; }

        [JsonPropertyName("contextLength")]
        public int ContextLength { get; set; }

        [JsonPropertyName("topology")]
        public string? Topology { get; set; }

        [JsonPropertyName("outputs")]
        public List<string>? Outputs { get; set; }

        public static PresetDto From(RunPreset p) => new()
        {
            Id = p.Id,
            Name = p.Name,
            Direction = DirectionWire(p.Direction),
            ContextLength = p.ContextLength,
            Topology = p.Topology.ToString().ToLowerInvariant(),
            Outputs = [.. OutputCatalog.ToWireNames(p.OutputFiles)],
        };

        public RunPreset? ToPreset()
        {
            var name = (Name ?? string.Empty).Trim();
            if (name.Length == 0 || ContextLength < 1 || Outputs is null)
            {
                return null;
            }

            if (!TryDirection(Direction, out var direction) || !Enum.TryParse<Topology>(Topology, ignoreCase: true, out var topology))
            {
                return null;
            }

            return new RunPreset(string.IsNullOrWhiteSpace(Id) ? "u-" + Guid.NewGuid().ToString("N")[..12] : Id, name.Length > MaxNameLength ? name[..MaxNameLength] : name, false, direction, ContextLength, topology, OutputCatalog.Normalize(OutputCatalog.FromWireNames(Outputs)));
        }

        private static string DirectionWire(Direction d) => d switch
        {
            Core.Direction.BothAveraged => "both-averaged",
            Core.Direction.BothSeparate => "both-separate",
            Core.Direction.ForwardOnly => "forward-only",
            Core.Direction.ReverseOnly => "reverse-only",
            _ => "both-combined",
        };

        private static bool TryDirection(string? wire, out Direction direction)
        {
            direction = Core.Direction.BothCombined;
            switch (wire)
            {
                case "both-combined": return true;
                case "both-averaged": direction = Core.Direction.BothAveraged; return true;
                case "both-separate": direction = Core.Direction.BothSeparate; return true;
                case "forward-only": direction = Core.Direction.ForwardOnly; return true;
                case "reverse-only": direction = Core.Direction.ReverseOnly; return true;
                default: return false;
            }
        }
    }
}
