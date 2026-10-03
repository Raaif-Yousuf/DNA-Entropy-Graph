using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #475: the release docs say the app publishes with <c>WindowsAppSDKSelfContained=true</c> but nothing set it,
/// so the unpackaged app silently depended on a Windows App SDK runtime installed on the lab PC.
/// MEASURED 2026-10-03: <c>dotnet publish -r win-x64 --self-contained true</c> without the property produced 252 files and no
/// <c>Microsoft.ui.xaml.dll</c>; with it, 441 files including <c>Microsoft.ui.xaml.dll</c>. A green build never shows the difference,
/// so the property is pinned here, in the App project itself (not only a flag in one workflow that a second publish path forgets).
/// </summary>
public class AppPublishPropertiesGuardTests
{
    private static string AppCsproj => Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "DnaEntropyGraph.App.csproj");

    [Fact]
    public void The_real_App_csproj_publishes_the_Windows_App_SDK_runtime_with_the_app()
    {
        File.Exists(AppCsproj).ShouldBeTrue("the guard must find the real App project, or it checks nothing.");

        PropertyValue(File.ReadAllText(AppCsproj), "WindowsAppSDKSelfContained").ShouldBe(
            "true",
            "WindowsAppSDKSelfContained must be true in DnaEntropyGraph.App.csproj, otherwise the installer needs the Windows App SDK " +
            "runtime already installed on the user's PC (#475).");
        PropertyValue(File.ReadAllText(AppCsproj), "WindowsPackageType").ShouldBe("None");
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><SelfContained>true</SelfContained></PropertyGroup></Project>", null)]
    [InlineData("<Project><PropertyGroup><WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained></PropertyGroup></Project>", "false")]
    [InlineData("<Project><PropertyGroup><WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained></PropertyGroup></Project>", "true")]
    public void The_scanner_reads_the_property_value_or_reports_it_absent(string csproj, string? expected)
    {
        PropertyValue(csproj, "WindowsAppSDKSelfContained").ShouldBe(expected);
    }

    private static string? PropertyValue(string csprojText, string name)
        => XDocument.Parse(csprojText).Descendants(name).Select(e => e.Value.Trim()).LastOrDefault();
}
