using DnaEntropyGraph.App.Controls;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App.Services;

/// <summary>Saves the entropy overview as a PNG on a white background, in light colours so it reads in a paper or a slide (issue #118). Never throws for a disk problem.</summary>
public sealed class OverviewPngExporter : IOverviewImageExporter
{
    private const int Width = 1600;
    private const int Height = 480;

    public bool Export(EntropyContig contig, string path)
    {
        try
        {
            var plot = EntropyPlotBuilder.Build(contig, dark: false);
            plot.FigureBackground.Color = ScottPlot.Colors.White;
            plot.DataBackground.Color = ScottPlot.Colors.White;
            plot.SavePng(path, Width, Height);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
