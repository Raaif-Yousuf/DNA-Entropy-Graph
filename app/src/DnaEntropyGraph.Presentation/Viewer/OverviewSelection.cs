using DnaEntropyGraph.Core.Tracks;

namespace DnaEntropyGraph.Presentation.Viewer;

/// <summary>A range dragged on the Results overview strip, in bases along the contig (the chart's x axis; 0 is the start of the contig). Either order.</summary>
public readonly record struct OverviewRange(double Start, double End);

/// <summary>
/// What the Viewer page is asked to open (issue #699): a run folder and, optionally, a locus (<c>name:start-end</c>, 1-based and inclusive,
/// as igv.js takes it). The page key's parameter is either this or, as before, a plain run folder string.
/// </summary>
public sealed record ViewerRunTarget(string RunFolder, string? Locus = null)
{
    /// <summary>The record name part of <see cref="Locus"/> (the last colon splits, so a name may hold colons); null when there is no locus.</summary>
    public string? Contig => Locus is { } l && l.LastIndexOf(':') is var i and > 0 ? l[..i] : null;
}

/// <summary>Turns a drag on the overview strip into a viewer target. Pure, so the clamping and the click rule are unit tested.</summary>
public static class OverviewSelection
{
    /// <summary>
    /// The 1-based inclusive range covering the dragged span, clamped to the contig. Null for a click (a span under one base), a span with
    /// no number in it, or one wholly outside the contig: those open nothing.
    /// </summary>
    public static ViewerRunTarget? ToTarget(string runFolder, EntropyContig contig, OverviewRange range)
    {
        if (double.IsNaN(range.Start) || double.IsNaN(range.End) || contig.Length < 1)
        {
            return null;
        }

        var lo = Math.Clamp(Math.Min(range.Start, range.End), 0, contig.Length);
        var hi = Math.Clamp(Math.Max(range.Start, range.End), 0, contig.Length);
        if (hi - lo < 1)
        {
            return null;
        }

        var start = (long)Math.Floor(lo) + 1;
        var end = (long)Math.Ceiling(hi);
        return new ViewerRunTarget(runFolder, $"{contig.Name}:{start}-{end}");
    }
}
