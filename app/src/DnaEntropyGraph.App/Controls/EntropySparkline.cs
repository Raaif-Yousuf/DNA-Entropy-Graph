using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Palettes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DnaEntropyGraph.App.Controls;

/// <summary>
/// The small entropy line on a Runs card (issue #118): a plain Polyline, not a chart control, because a card list can hold
/// hundreds and each ScottPlot control owns a GPU surface. Values are bits (0 to 2) and fill the control's own Width and Height.
/// A NaN (a stretch with no data) repeats the last good value so the line stays in one piece. Not a *.xaml.cs file (Hard Rule 8
/// concerns code-behind of a XAML page); this is drawing only.
/// </summary>
public sealed class EntropySparkline : Grid
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(float[]), typeof(EntropySparkline), new PropertyMetadata(null, (d, _) => ((EntropySparkline)d).Redraw()));

    private readonly Polyline _line = new()
    {
        StrokeThickness = 1.5,
        StrokeLineJoin = PenLineJoin.Round,
    };

    private bool _strokeSet;

    /// <summary>An explicit colour. Left unset, the line takes the chosen palette's entropy colour (issue #195), read each time it is drawn.</summary>
    public Brush? Stroke
    {
        get => _line.Stroke;
        set
        {
            _strokeSet = value is not null;
            _line.Stroke = value;
        }
    }

    public EntropySparkline()
    {
        Width = 120;
        Height = 28;
        Children.Add(_line);
        Loaded += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    private void PaintFromPalette()
    {
        if (_strokeSet)
        {
            return;
        }

        var hex = AppPalette.Current.Hex(TrackRole.Entropy, ActualTheme == ElementTheme.Dark);
        var (r, g, b) = PaletteValidator.ParseHex(hex);
        _line.Stroke = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, (byte)r, (byte)g, (byte)b));
    }

    public float[]? Values
    {
        get => (float[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    private void Redraw()
    {
        PaintFromPalette();
        var points = new PointCollection();
        if (Values is { Length: > 1 } values)
        {
            var last = 0f;
            for (var i = 0; i < values.Length; i++)
            {
                last = float.IsNaN(values[i]) ? last : values[i];
                var x = Width * i / (values.Length - 1);
                var y = Height * (1 - Math.Clamp(last / 2.0, 0, 1));
                points.Add(new Point(x, y));
            }
        }

        _line.Points = points;
    }
}
