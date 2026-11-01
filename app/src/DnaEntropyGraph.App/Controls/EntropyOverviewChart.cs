using System.Windows.Input;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Palettes;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Viewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ScottPlot.WinUI;

namespace DnaEntropyGraph.App.Controls;

/// <summary>
/// The entropy overview strip on the Results page (issue #118): a ScottPlot chart of one contig, drawn from the binned data the
/// ViewModel already holds, so it shows within a frame of the page and never waits for the WebView2 viewer. Bind
/// <see cref="Contig"/>; it redraws when that changes and when the app theme does. Look only: mouse zoom and pan are off, so the
/// strip cannot be left in a state the user cannot get back from. Issue #699: a left-button drag along the strip shades the span and, on
/// release, executes <see cref="RangeSelectedCommand"/> with an <see cref="OverviewRange"/> in bases; what that means (clamping, the click
/// rule, opening the viewer) is the ViewModel's. Not a *.xaml.cs file: there is no XAML to hold, so the
/// "no logic in code-behind" guard (Hard Rule 8) does not apply, and what is here is drawing, not behaviour.
/// </summary>
public sealed class EntropyOverviewChart : UserControl
{
    public static readonly DependencyProperty ContigProperty = DependencyProperty.Register(
        nameof(Contig), typeof(EntropyContig), typeof(EntropyOverviewChart), new PropertyMetadata(null, (d, _) => ((EntropyOverviewChart)d).Redraw()));

    public static readonly DependencyProperty RangeSelectedCommandProperty = DependencyProperty.Register(
        nameof(RangeSelectedCommand), typeof(ICommand), typeof(EntropyOverviewChart), new PropertyMetadata(null));

    private readonly WinUIPlot _plot = new();
    private ScottPlot.Plottables.HorizontalSpan? _span;
    private readonly OverviewDrag _drag = new();

    public EntropyOverviewChart()
    {
        _plot.UserInputProcessor.Disable();
        Content = _plot;
        MinHeight = 140;
        ActualThemeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        _plot.PointerPressed += OnPointerPressed;
        _plot.PointerMoved += OnPointerMoved;
        _plot.PointerReleased += OnPointerReleased;
        _plot.PointerCanceled += (_, _) => AbortDrag();
        _plot.PointerCaptureLost += (_, _) => AbortDrag();
    }

    /// <summary>Executed with an <see cref="OverviewRange"/> (bases along the contig) when the user finishes a drag on the strip.</summary>
    public ICommand? RangeSelectedCommand
    {
        get => (ICommand?)GetValue(RangeSelectedCommandProperty);
        set => SetValue(RangeSelectedCommandProperty, value);
    }

    private double BaseAt(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_plot).Position;
        var scale = _plot.DisplayScale;
        return _plot.Plot.GetCoordinates(new ScottPlot.Pixel((float)(point.X * scale), (float)(point.Y * scale))).X;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Contig is null || !e.GetCurrentPoint(_plot).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _drag.Begin(BaseAt(e), e.GetCurrentPoint(_plot).Position.X);
        _plot.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag.StartBase is not { } start)
        {
            return;
        }

        var now = BaseAt(e);
        if (_span is null)
        {
            _span = _plot.Plot.Add.HorizontalSpan(start, now);
            _span.FillColor = ScottPlot.Color.FromHex(AppPalette.Current.Hex(TrackRole.Marker, ActualTheme == ElementTheme.Dark)).WithAlpha(0.25);
        }

        _span.X1 = Math.Min(start, now);
        _span.X2 = Math.Max(start, now);
        _plot.Refresh();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // The range first: ReleasePointerCapture may raise PointerCaptureLost synchronously, which aborts the drag.
        var range = _drag.Release(BaseAt(e), e.GetCurrentPoint(_plot).Position.X);
        _plot.ReleasePointerCapture(e.Pointer);
        HideSpan();
        if (range is { } dragged && RangeSelectedCommand is { } command && command.CanExecute(dragged))
        {
            command.Execute(dragged);
        }
    }

    private void AbortDrag()
    {
        _drag.Cancel();
        HideSpan();
    }

    private void HideSpan()
    {
        if (_span is not null)
        {
            _plot.Plot.Remove(_span);
            _span = null;
            _plot.Refresh();
        }
    }

    public EntropyContig? Contig
    {
        get => (EntropyContig?)GetValue(ContigProperty);
        set => SetValue(ContigProperty, value);
    }

    private void Redraw()
    {
        _span = null;
        _drag.Cancel();
        if (Contig is not { } contig)
        {
            _plot.Reset(new ScottPlot.Plot());
            _plot.Refresh();
            return;
        }

        var dark = ActualTheme == ElementTheme.Dark;
        _plot.Reset(EntropyPlotBuilder.Build(contig, dark, AppPalette.Current.For(dark)));
        _plot.Refresh();
    }
}
