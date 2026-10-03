using Microsoft.UI.Xaml;
using Windows.UI;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Colours for the caption buttons of the extended title bar, from the app's theme (#639). The system draws those
/// buttons in the SYSTEM theme, not the content's, so an app theme that differs from it needs these or the glyphs are
/// invisible. A pure mapping so it is tested without a window.
/// </summary>
public sealed record CaptionButtonPalette(Color Foreground, Color HoverBackground, Color PressedBackground, Color InactiveForeground)
{
    private static readonly CaptionButtonPalette LightPalette = new(
        Color.FromArgb(255, 0, 0, 0), Color.FromArgb(25, 0, 0, 0), Color.FromArgb(51, 0, 0, 0), Color.FromArgb(255, 138, 138, 138));

    private static readonly CaptionButtonPalette DarkPalette = new(
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(25, 255, 255, 255), Color.FromArgb(51, 255, 255, 255), Color.FromArgb(255, 138, 138, 138));

    /// <summary>Null for <see cref="ElementTheme.Default"/>: following the system, the system's own colours are already right.</summary>
    public static CaptionButtonPalette? For(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => LightPalette,
        ElementTheme.Dark => DarkPalette,
        _ => null,
    };
}
