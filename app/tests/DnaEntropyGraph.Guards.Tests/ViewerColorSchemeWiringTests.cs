using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #115, wired-to-nothing: the viewer's dark.css is driven by a bridge theme command, but the WebView2 page's own
/// prefers-color-scheme (igv.js dialogs, scrollbars, form controls) only follows the app when the host sets
/// CoreWebView2.Profile.PreferredColorScheme. The host is WinUI code no unit test can run, so this reads its source: the
/// scheme must be set when the viewer starts and again on every ActualThemeChanged.
/// </summary>
public class ViewerColorSchemeWiringTests
{
    private static string HostSource()
    {
        var path = Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Services", "IgvViewerHost.cs");
        File.Exists(path).ShouldBeTrue("the guard must find the real host, or it checks nothing.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_host_sets_the_preferred_color_scheme_when_the_viewer_starts_and_when_the_theme_changes()
    {
        var source = HostSource();

        source.ShouldContain("ApplyColorScheme(");
        System.Text.RegularExpressions.Regex.Matches(source, @"\bApplyColorScheme\(").Count
            .ShouldBeGreaterThanOrEqualTo(3, "one definition, one call in Configure, one in OnActualThemeChanged.");
        source.ShouldContain("Profile.PreferredColorScheme");
        source.ShouldContain("CoreWebView2PreferredColorScheme.Dark");
        source.ShouldContain("CoreWebView2PreferredColorScheme.Light");
    }
}
