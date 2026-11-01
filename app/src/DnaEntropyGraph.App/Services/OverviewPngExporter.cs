using DnaEntropyGraph.App.Controls;
using DnaEntropyGraph.Core.Palettes;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App.Services;

/// <summary>Saves the entropy overview as a PNG on a white background, in light colours so it reads in a paper or a slide (issue #118). Never throws for a disk problem.</summary>
public sealed class OverviewPngExporter : IOverviewImageExporter
{
    private const int Width = 1600;
    private const int Height = 480;

    private readonly Action<ScottPlot.Plot, string, int, int> _save;
    private readonly ITrackPaletteProvider _palettes;

    /// <summary>Issue #195: the palette is read from <paramref name="palettes"/> at each export; without one, the default.</summary>
    public OverviewPngExporter(ITrackPaletteProvider? palettes = null)
        : this((plot, path, width, height) => plot.SavePng(path, width, height), palettes)
    {
    }

    /// <summary>Takes the save step as a seam so a test can make it fail half way.</summary>
    public OverviewPngExporter(Action<ScottPlot.Plot, string, int, int> save, ITrackPaletteProvider? palettes = null)
    {
        _save = save;
        _palettes = palettes ?? new FixedPalette();
    }

    private sealed class FixedPalette : ITrackPaletteProvider
    {
        public TrackPalette Current => TrackPalettes.Default;
    }

    public bool Export(EntropyContig contig, OverviewImageLabels labels, string path)
    {
        // Only a file this call is about to create may be taken away again if the save fails half way; one already there is the user's.
        var existed = Path.Exists(path);
        try
        {
            var plot = EntropyPlotBuilder.Build(contig, dark: false, _palettes.Current.For(dark: false));
            plot.FigureBackground.Color = ScottPlot.Colors.White;
            plot.DataBackground.Color = ScottPlot.Colors.White;
            plot.Title(labels.Title);
            plot.XLabel(labels.XAxis);
            plot.YLabel(labels.YAxis);
            _save(plot, path, Width, Height);
            return true;
        }
        catch (Exception ex)
        {
            if (!existed)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Nothing more can be done about it; the caller still gets the failure.
                }
            }

            if (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            throw;
        }
    }
}
