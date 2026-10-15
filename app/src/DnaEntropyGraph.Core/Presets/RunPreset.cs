namespace DnaEntropyGraph.Core.Presets;

/// <summary>
/// A named set of run options (issue #136): how the two strands combine, how much context the model sees, whether the
/// sequence is a circle, and which files come back. Everything else (the model, the machine, the lifecycle) stays
/// whatever the run would have used.
/// </summary>
/// <param name="Id">Stable id: a fixed word for a built-in, <c>u-</c> and a guid for the user's own.</param>
/// <param name="Name">The name to show. For a built-in it is the en-US name, which a guard pins to <c>Preset_&lt;id&gt;_Name</c> in Resources.resw.</param>
/// <param name="BuiltIn">True for the presets that ship with the app; they cannot be changed or deleted.</param>
public sealed record RunPreset(string Id, string Name, bool BuiltIn, Direction Direction, int ContextLength, Topology Topology, OutputFileKinds OutputFiles)
{
    /// <summary>The options with this preset's choices applied and the preset recorded; every other option is kept.</summary>
    public RunOptions ApplyTo(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var outputs = OutputCatalog.Normalize(OutputFiles);
        return options with
        {
            Direction = Direction,
            ContextLength = ContextLength,
            Topology = Topology,
            OutputFiles = outputs,
            TrackFormat = outputs.HasFlag(OutputFileKinds.Wig) && !outputs.HasFlag(OutputFileKinds.BedGraph) ? TrackFormat.Wig : TrackFormat.BedGraph,
            PresetId = Id,
            PresetName = Name,
        };
    }
}

/// <summary>The presets that ship with the app, documented in docs/user_guide/03-run-a-sequence.md.</summary>
public static class BuiltInPresets
{
    /// <summary>One pass over the forward strand and the few files a first look needs: the cheapest, fastest run.</summary>
    public static RunPreset QuickLook { get; } = new(
        "quick_look", "Quick look", true, Direction.ForwardOnly, 4096, Topology.Auto,
        OutputFileKinds.BedGraph | OutputFileKinds.Stats | OutputFileKinds.GenBank);

    /// <summary>Both strands combined and every default file, with the smoothed tracks, region calls and gene summary a figure draws on.</summary>
    public static RunPreset Publication { get; } = new(
        "publication", "Publication", true, Direction.BothCombined, 4096, Topology.Auto, OutputCatalog.Defaults);

    /// <summary>A circular plasmid read on both strands with no seam, the low and high entropy regions called, and the files a QC check reads.</summary>
    public static RunPreset PlasmidQc { get; } = new(
        "plasmid_qc", "Plasmid QC", true, Direction.BothCombined, 4096, Topology.Circular,
        OutputFileKinds.GenBank | OutputFileKinds.BedGraph | OutputFileKinds.Regions | OutputFileKinds.Stats | OutputFileKinds.GeneSummary | OutputFileKinds.GenesGff3);

    public static IReadOnlyList<RunPreset> All { get; } = [QuickLook, Publication, PlasmidQc];

    /// <summary>The <c>Resources.resw</c> key of a built-in's name (and, with <c>_Detail</c>, its one-line description).</summary>
    public static string NameKey(string id) => "Preset_" + id + "_Name";

    public static string DetailKey(string id) => "Preset_" + id + "_Detail";
}
