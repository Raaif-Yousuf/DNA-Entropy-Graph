using DnaEntropyGraph.Presentation.Services;
using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Live theme change (#639): sets <c>RequestedTheme</c> on the shell's root element, which every page and the WebView2
/// viewer (through its <c>ActualThemeChanged</c> subscription in <see cref="IgvViewerHost"/>) inherit. The saved theme at
/// startup is still applied by <see cref="ThemeApplier.Apply"/>; MainWindow hands this service the same root.
/// </summary>
public sealed class WindowThemeApplier : IThemeApplier
{
    private FrameworkElement? _root;

    public void Attach(FrameworkElement root) => _root = root;

    public void Apply(string theme)
    {
        if (_root is not null)
        {
            _root.RequestedTheme = ThemeApplier.Resolve(theme);
        }
    }
}
