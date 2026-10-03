using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core;

/// <summary>
/// The output files the user last chose, remembered per input group (issue #103). The New run page reads it as its
/// starting state and writes it when a run starts; the Settings page (#104) binds the same store for its defaults.
/// Stored as the worker's names, comma separated, so a reordered or grown enum cannot change what a saved choice means.
/// </summary>
public sealed class OutputSelectionStore
{
    private readonly ISettingsStore _settings;

    public OutputSelectionStore(ISettingsStore settings) => _settings = settings;

    /// <summary>The settings key for a group (<c>output_files_genbank</c> / <c>output_files_sequence</c>).</summary>
    public static string KeyFor(OutputInputKinds group) => "output_files_" + group.ToString().ToLowerInvariant();

    /// <summary>What was last chosen for the group, or the catalog defaults when nothing usable is stored.</summary>
    public OutputFileKinds Load(OutputInputKinds group)
    {
        var stored = _settings.GetString(KeyFor(group));
        var names = (stored ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var offered = OutputCatalog.For(group).Aggregate(OutputFileKinds.None, (all, e) => all | e.Kind);
        var chosen = OutputCatalog.FromWireNames(names) & offered;
        return chosen == OutputFileKinds.None ? OutputCatalog.Defaults & offered : chosen;
    }

    /// <summary>Remembers the choice for the group. An empty choice is refused: a run must return at least one file.</summary>
    public void Save(OutputInputKinds group, OutputFileKinds selection)
    {
        var offered = OutputCatalog.For(group).Aggregate(OutputFileKinds.None, (all, e) => all | e.Kind);
        var names = OutputCatalog.ToWireNames(selection & offered);
        if (names.Count == 0)
        {
            throw new ArgumentException("Choose at least one output file.", nameof(selection));
        }

        _settings.SetString(KeyFor(group), string.Join(',', names));
    }
}
