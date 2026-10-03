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
    /// <summary>The only condition under which the value may apply: `dotnet publish` sets _IsPublishing. A different (or never-true) condition would pass a value-only check and ship nothing.</summary>
    private const string PublishOnlyCondition = "'$(_IsPublishing)' == 'true'";

    private const string NeverTrue = "<Project><PropertyGroup><WindowsAppSDKSelfContained Condition=\"'$(Never)' == 'x'\">true</WindowsAppSDKSelfContained></PropertyGroup></Project>";

    private static string AppCsproj => Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "DnaEntropyGraph.App.csproj");

    [Fact]
    public void The_real_App_csproj_publishes_the_Windows_App_SDK_runtime_with_the_app()
    {
        File.Exists(AppCsproj).ShouldBeTrue("the guard must find the real App project, or it checks nothing.");
        var text = File.ReadAllText(AppCsproj);

        Property(text, "WindowsAppSDKSelfContained").ShouldBe(
            ("true", PublishOnlyCondition),
            "WindowsAppSDKSelfContained must be true exactly under the _IsPublishing condition in DnaEntropyGraph.App.csproj; " +
            "otherwise the installer needs the Windows App SDK runtime already installed on the user's PC (#475).");
        Property(text, "WindowsPackageType")!.Value.Value.ShouldBe("None");
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><SelfContained>true</SelfContained></PropertyGroup></Project>", null, null)]
    [InlineData("<Project><PropertyGroup><WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained></PropertyGroup></Project>", "false", null)]
    [InlineData("<Project><PropertyGroup><WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained></PropertyGroup></Project>", "true", null)]
    [InlineData(NeverTrue, "true", "'$(Never)' == 'x'")]
    public void The_scanner_reads_the_value_and_the_condition_or_reports_the_property_absent(string csproj, string? value, string? condition)
    {
        var read = Property(csproj, "WindowsAppSDKSelfContained");
        if (value is null)
        {
            read.ShouldBeNull();
            return;
        }

        read.ShouldBe((value, condition));
    }

    [Fact]
    public void A_never_true_condition_is_not_the_publish_condition()
    {
        Property(NeverTrue, "WindowsAppSDKSelfContained").ShouldNotBe(("true", PublishOnlyCondition));
    }

    private static (string Value, string? Condition)? Property(string csprojText, string name)
    {
        var e = XDocument.Parse(csprojText).Descendants(name).LastOrDefault();
        return e is null ? null : (e.Value.Trim(), e.Attribute("Condition")?.Value);
    }
}
