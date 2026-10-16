using DnaEntropyGraph.Core.Tracks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScottPlot.WinUI;

namespace DnaEntropyGraph.App.Controls;

/// <summary>
/// The entropy overview strip on the Results page (issue #118): a ScottPlot chart of one contig, drawn from the binned data the
/// ViewModel already holds, so it shows within a frame of the page and never waits for the WebView2 viewer. Bind
/// <see cref="Contig"/>; it redraws when that changes and when the app theme does. Look only: mouse zoom and pan are off, so the
/// strip cannot be left in a state the user cannot get back from. Not a *.xaml.cs file: there is no XAML to hold, so the
/// "no logic in code-behind" guard (Hard Rule 8) does not apply, and what is here is drawing, not behaviour.
/// </summary>
public sealed class EntropyOverviewChart : UserControl
{
    public static readonly DependencyProperty ContigProperty = DependencyProperty.Register(
        nameof(Contig), typeof(EntropyContig), typeof(EntropyOverviewChart), new PropertyMetadata(null, (d, _) => ((EntropyOverviewChart)d).Redraw()));

    private readonly WinUIPlot _plot = new();

    public EntropyOverviewChart()
    {
        _plot.UserInputProcessor.Disable();
        Content = _plot;
        MinHeight = 140;
        ActualThemeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    public EntropyContig? Contig
    {
        get => (EntropyContig?)GetValue(ContigProperty);
        set => SetValue(ContigProperty, value);
    }

    private void Redraw()
    {
        if (Contig is not { } contig)
        {
            _plot.Reset(new ScottPlot.Plot());
            _plot.Refresh();
            return;
        }

        _plot.Reset(EntropyPlotBuilder.Build(contig, dark: ActualTheme == ElementTheme.Dark));
        _plot.Refresh();
    }
}
