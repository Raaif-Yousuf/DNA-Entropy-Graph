using System.Windows.Input;
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
    private double? _dragStart;

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
        _plot.PointerCanceled += (_, _) => EndDrag(null);
        _plot.PointerCaptureLost += (_, _) => EndDrag(null);
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

        _dragStart = BaseAt(e);
        _plot.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        var now = BaseAt(e);
        if (_span is null)
        {
            _span = _plot.Plot.Add.HorizontalSpan(start, now);
            _span.FillColor = ScottPlot.Colors.Orange.WithAlpha(0.25);
        }

        _span.X1 = Math.Min(start, now);
        _span.X2 = Math.Max(start, now);
        _plot.Refresh();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var end = _dragStart is null ? (double?)null : BaseAt(e);
        _plot.ReleasePointerCapture(e.Pointer);
        EndDrag(end);
    }

    private void EndDrag(double? end)
    {
        var start = _dragStart;
        _dragStart = null;
        if (_span is not null)
        {
            _plot.Plot.Remove(_span);
            _span = null;
            _plot.Refresh();
        }

        if (start is { } from && end is { } to && RangeSelectedCommand is { } command)
        {
            var range = new OverviewRange(from, to);
            if (command.CanExecute(range))
            {
                command.Execute(range);
            }
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
        _dragStart = null;
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
