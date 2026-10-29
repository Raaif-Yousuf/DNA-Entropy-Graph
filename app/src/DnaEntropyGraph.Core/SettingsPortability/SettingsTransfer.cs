using System.Text.Json;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Presets;

namespace DnaEntropyGraph.Core.SettingsPortability;

public enum SettingsImportStatus
{
    /// <summary>The file is ours and has something to apply.</summary>
    Ok,

    /// <summary>The text is not a JSON object.</summary>
    NotJson,

    /// <summary>JSON, but not a settings file from this app.</summary>
    NotOurFile,

    /// <summary>Written by a newer version of the app than this one.</summary>
    NewerVersion,

    /// <summary>Ours and readable, but it changes nothing here.</summary>
    NothingToImport,
}

/// <summary>One setting an import would change: <paramref name="Current"/> is null when this PC has no saved value (it uses the default).</summary>
public sealed record SettingsChange(string Key, string LabelKey, string? Current, string Incoming);

/// <summary>What an import would do. Nothing is written until <see cref="SettingsTransfer.Apply"/>.</summary>
public sealed record SettingsImportPlan(
    SettingsImportStatus Status,
    IReadOnlyList<SettingsChange> Changes,
    int PresetCount,
    int IgnoredCount,
    int RejectedCount,
    string? PresetsJson)
{
    public static SettingsImportPlan Failed(SettingsImportStatus status) => new(status, [], 0, 0, 0, null);
}

/// <summary>
/// Export and import of the app's settings and the user's presets as one versioned JSON file (issue #242). Export writes only the
/// <see cref="SettingsCatalog.Exported"/> allowlist; import validates the whole file into a <see cref="SettingsImportPlan"/> (the diff
/// preview) and only <see cref="Apply"/> writes. A file from a newer version is refused, never half-read.
/// </summary>
public sealed class SettingsTransfer
{
    public const string FormatName = "dna-entropy-graph-settings";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ISettingsStore _settings;
    private readonly PresetStore _presets;

    public SettingsTransfer(ISettingsStore settings, PresetStore presets)
    {
        _settings = settings;
        _presets = presets;
    }

    /// <summary>The allowlisted settings that have a saved value, plus the user's own presets, as JSON text.</summary>
    public string Export()
    {
        var values = new JsonObject();
        foreach (var entry in SettingsCatalog.Exported)
        {
            var value = _settings.GetString(entry.Key);
            if (!string.IsNullOrEmpty(value) && entry.IsValid(value))
            {
                values[entry.Key] = value;
            }
        }

        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["schema"] = SchemaVersion,
            ["settings"] = values,
            ["presets"] = JsonNode.Parse(_presets.ExportJson()),
        };
        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary>Reads and validates <paramref name="json"/> and says what applying it would change. Writes nothing.</summary>
    public SettingsImportPlan Plan(string? json)
    {
        try
        {
            return PlanCore(json);
        }
        catch (ArgumentException)
        {
            // MEASURED 2026-10-16 (.NET 10): JsonNode.Parse accepts a duplicate key and throws ArgumentException later, on first access
            // (at any nesting level), so the whole read is guarded, not just the parse.
            return SettingsImportPlan.Failed(SettingsImportStatus.NotJson);
        }
    }

    private SettingsImportPlan PlanCore(string? json)
    {
        JsonObject? root;
        try
        {
            root = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            return SettingsImportPlan.Failed(SettingsImportStatus.NotJson);
        }

        if (root["format"] is not JsonValue format || !format.TryGetValue<string>(out var name) || name != FormatName
            || root["schema"] is not JsonValue schemaNode || !schemaNode.TryGetValue<int>(out var schema) || schema < 1)
        {
            return SettingsImportPlan.Failed(SettingsImportStatus.NotOurFile);
        }

        if (schema > SchemaVersion)
        {
            return SettingsImportPlan.Failed(SettingsImportStatus.NewerVersion);
        }

        var changes = new List<SettingsChange>();
        int ignored = 0, rejected = 0;
        if (root["settings"] is JsonObject settings)
        {
            foreach (var (key, node) in settings)
            {
                var entry = SettingsCatalog.Find(key);
                if (entry is null)
                {
                    ignored++;
                    continue;
                }

                if (node is not JsonValue v || !v.TryGetValue<string>(out var incoming) || !entry.IsValid(incoming))
                {
                    rejected++;
                    continue;
                }

                var current = _settings.GetString(key);
                if (current != incoming)
                {
                    changes.Add(new SettingsChange(key, entry.LabelKey, current, incoming));
                }
            }
        }

        var presetCount = 0;
        string? presetsJson = null;
        if (root["presets"] is JsonObject presets)
        {
            // A dry run on a scratch copy of this PC's own presets, so name collisions (an identical preset is not a change; a
            // repeated name in the file counts once) are counted as Import will really apply them.
            var scratch = new ScratchSettings();
            var real = _settings.GetString(PresetStore.SettingsKey);
            if (real is not null)
            {
                scratch.SetString(PresetStore.SettingsKey, real);
            }

            var scratchStore = new PresetStore(scratch);
            var before = scratchStore.List().Where(p => !p.BuiltIn).Select(p => p with { Id = string.Empty }).ToHashSet();
            var dryRun = scratchStore.Import(presets.ToJsonString());
            presetCount = scratchStore.List().Where(p => !p.BuiltIn).Select(p => p with { Id = string.Empty }).Distinct().Count(p => !before.Contains(p));
            presetsJson = dryRun.Imported == 0 || presetCount == 0 ? null : presets.ToJsonString();
        }

        var status = changes.Count == 0 && presetCount == 0 ? SettingsImportStatus.NothingToImport : SettingsImportStatus.Ok;
        return new SettingsImportPlan(status, changes, presetCount, ignored, rejected, presetsJson);
    }

    /// <summary>Writes the plan's settings and presets. Returns how many settings were written; a plan that is not Ok writes nothing.</summary>
    /// <exception cref="SettingsUnavailableException">The settings file is in use by another copy of the app.</exception>
    public int Apply(SettingsImportPlan plan)
    {
        if (plan.Status != SettingsImportStatus.Ok)
        {
            return 0;
        }

        foreach (var change in plan.Changes)
        {
            _settings.SetString(change.Key, change.Incoming);
        }

        if (plan.PresetsJson is not null)
        {
            _presets.Import(plan.PresetsJson);
        }

        return plan.Changes.Count;
    }

    private sealed class ScratchSettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value) => _values[key] = value;
    }
}
