using System.Reflection;
using System.Xml.Linq;
using DnaEntropyGraph.App.Startup;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #438. MEASURED 2026-10-03 (minidump of PR #529's CI run, windows-latest): the one test still running at
/// the 4 minute hang timeout was parked in <c>MddBootstrapInitialize2</c>, reached from <c>&lt;Module&gt;..cctor</c>
/// of the App assembly (the Windows App SDK's generated <c>AutoInitialize</c>) the first time the test touched an App
/// type. Its default option is <c>OnNoMatch_ShowUI</c>: with no Windows App Runtime installed it opens a modal "install
/// the runtime" dialog, and a CI runner has no desktop to answer it, so the call waits forever. In CI (<c>CI=true</c>,
/// set by GitHub Actions) the App is built with option <c>None</c>, which fails fast with the HRESULT instead.
/// Developer machines keep the default, which is the friendly path for a person at a desktop.
/// </summary>
public class BootstrapNeverShowsUiGuardTests
{
    private const string AutoInitializeType = "Microsoft.Windows.ApplicationModel.DynamicDependency.BootstrapCS.AutoInitialize";

    private static readonly string AppCsproj = Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "DnaEntropyGraph.App.csproj");

    private static string? OptionsOfTheBuiltApp()
    {
        var type = typeof(ServiceRegistration).Assembly.GetType(AutoInitializeType);
        type.ShouldNotBeNull("vacuity: the auto-initializer must be compiled into the App assembly, or this guard checks nothing and the hang cause moved.");
        return type.GetProperty("Options", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!.ToString();
    }

    [Fact]
    public void A_CI_build_of_the_App_bootstraps_without_ever_showing_a_dialog()
    {
        var options = OptionsOfTheBuiltApp();

        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            options.ShouldBe("None", "any option containing OnNoMatch_ShowUI blocks forever on a runner with no desktop (#438).");
        }
        else
        {
            options.ShouldBe("OnNoMatch_ShowUI", "outside CI the default dialog stays; if this fails the Windows App SDK changed its default, re-read #438.");
        }
    }

    [Fact]
    public void The_App_csproj_turns_the_bootstrap_dialog_off_only_when_CI_is_true()
    {
        File.Exists(AppCsproj).ShouldBeTrue();

        var element = XDocument.Load(AppCsproj).Descendants("WindowsAppSDKBootstrapAutoInitializeOptions_None").ShouldHaveSingleItem();

        element.Value.ShouldBe("true");
        element.Attribute("Condition")?.Value.ShouldBe("'$(CI)' == 'true'");
    }
}
