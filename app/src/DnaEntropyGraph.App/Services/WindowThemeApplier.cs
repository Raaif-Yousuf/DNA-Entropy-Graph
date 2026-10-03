using DnaEntropyGraph.Presentation.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Live theme change (#639): sets <c>RequestedTheme</c> on the window's content root, which every page, the title bar, the
/// Mica backdrop and the WebView2 viewer (through its <c>ActualThemeChanged</c> subscription in <see cref="IgvViewerHost"/>)
/// inherit, and recolours the caption buttons the system draws (<see cref="CaptionButtonPalette"/>). MainWindow also calls
/// <see cref="Apply"/> with the saved theme at startup, so launch and a live change take the same path.
/// </summary>
public sealed class WindowThemeApplier : IThemeApplier
{
    private FrameworkElement? _root;
    private AppWindowTitleBar? _titleBar;

    public void Attach(FrameworkElement root, AppWindow appWindow)
    {
        _root = root;
        _titleBar = appWindow.TitleBar;
    }

    public void Apply(string theme)
    {
        var resolved = ThemeApplier.Resolve(theme);
        if (_root is not null)
        {
            _root.RequestedTheme = resolved;
        }

        if (_titleBar is not null)
        {
            // A null palette (follow the system) resets every colour to the system default.
            var palette = CaptionButtonPalette.For(resolved);
            _titleBar.ButtonForegroundColor = palette?.Foreground;
            _titleBar.ButtonHoverForegroundColor = palette?.Foreground;
            _titleBar.ButtonPressedForegroundColor = palette?.Foreground;
            _titleBar.ButtonInactiveForegroundColor = palette?.InactiveForeground;
            _titleBar.ButtonHoverBackgroundColor = palette?.HoverBackground;
            _titleBar.ButtonPressedBackgroundColor = palette?.PressedBackground;
        }
    }
}
