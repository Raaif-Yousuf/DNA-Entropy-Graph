using DnaEntropyGraph.Core.Tracks;
using ScottPlot;

namespace DnaEntropyGraph.App.Controls;

/// <summary>
/// Builds the entropy overview as a ScottPlot <see cref="Plot"/> (issue #118): one Signal line of the binned means (0 to 2 bits)
/// along the contig, and, when the run recorded one, a dashed vertical line at the seam where the forward and reverse passes were
/// joined. The on-screen chart and the PNG export both come from here, so the picture saved is the picture shown. Plain library
/// calls, no WinUI types: it runs in the exporter without a window.
/// </summary>
public static class EntropyPlotBuilder
{
    /// <summary>The scale an entropy value can take (Hard Rule 3): 0 to 2 bits.</summary>
    public const double MaxBits = 2.0;

    public static Plot Build(EntropyContig contig, bool dark)
    {
        var plot = new Plot();
        var values = new double[contig.Means.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = contig.Means[i];
        }

        var signal = plot.Add.Signal(values, contig.BinSize);
        signal.Data.XOffset = contig.BinSize / 2.0;
        signal.LineWidth = 1.5f;
        signal.Color = dark ? Color.FromHex("#6CB6FF") : Color.FromHex("#0F6CBD");

        if (contig.Seam is { } seam)
        {
            var line = plot.Add.VerticalLine(seam);
            line.LinePattern = LinePattern.Dashed;
            line.LineWidth = 1.5f;
            line.Color = dark ? Color.FromHex("#FFB86C") : Color.FromHex("#B54708");
        }

        plot.Axes.SetLimitsX(0, Math.Max(1, contig.Length));
        plot.Axes.SetLimitsY(0, MaxBits);

        // The chart sits on the page's own background, so the figure and data area are transparent on screen; the exporter
        // paints white behind it.
        plot.FigureBackground.Color = Colors.Transparent;
        plot.DataBackground.Color = Colors.Transparent;
        var ink = dark ? Colors.White : Colors.Black;
        plot.Axes.Color(ink);
        plot.Grid.MajorLineColor = ink.WithAlpha(0.15);
        return plot;
    }
}
