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
/// the runtime" dialog, and a CI runner has no desktop to answer it, so the call waits forever. The App is therefore
/// built with option <c>None</c> in every configuration (a missing runtime then ends the process with its HRESULT).
/// Unconditional on purpose: the shipped app is published self-contained, which drops the auto-initializer, so the
/// dialog only ever existed in dev and test builds, and one behaviour everywhere means a laptop reproduces CI.
/// MEASURED 2026-10-03: the bootstrap cannot simply be switched off (WindowsAppSdkBootstrapInitialize=false): the
/// Guards DiResolutionTests then die with COMException 0x80040154 in DispatcherAdapter, so the runtime must still be
/// installed where tests run (ci-app.yml installs and asserts it).
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
    public void The_App_bootstraps_without_ever_showing_a_dialog()
    {
        // Read from the compiled App assembly, so this is the value the build really used, independent of the test-time environment.
        OptionsOfTheBuiltApp().ShouldBe("None", "any option containing OnNoMatch_ShowUI blocks forever on a runner with no desktop (#438).");
    }

    [Fact]
    public void The_App_csproj_sets_the_bootstrap_option_to_None_unconditionally()
    {
        File.Exists(AppCsproj).ShouldBeTrue();

        var element = XDocument.Load(AppCsproj).Descendants("WindowsAppSDKBootstrapAutoInitializeOptions_None").ShouldHaveSingleItem();

        element.Value.ShouldBe("true");
        element.Attribute("Condition").ShouldBeNull("a CI-only condition makes a laptop differ from CI; see the class summary.");
    }
}
