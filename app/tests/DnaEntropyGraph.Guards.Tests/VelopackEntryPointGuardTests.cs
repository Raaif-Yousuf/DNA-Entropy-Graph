using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #513: <c>vpk pack</c> refuses an app whose entry point does not call <c>VelopackApp.Build().Run()</c> ("Unable to verify
/// VelopackApp is called", MEASURED 2026-10-03, vpk 1.2.161), and it must run before any UI starts so install/uninstall/update hooks
/// can exit early. A WinUI 3 app's generated Main hides the entry point, so the project must disable it and own a Program.Main.
/// </summary>
public class VelopackEntryPointGuardTests
{
    private static string AppDir => Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App");

    [Fact]
    public void The_App_owns_its_entry_point_and_calls_VelopackApp_before_starting_the_UI()
    {
        var csproj = File.ReadAllText(Path.Combine(AppDir, "DnaEntropyGraph.App.csproj"));
        csproj.ShouldContain("DISABLE_XAML_GENERATED_MAIN", Case.Sensitive, "without it the XAML-generated Main wins and our Program.Main never runs.");

        var programPath = Path.Combine(AppDir, "Program.cs");
        File.Exists(programPath).ShouldBeTrue("the App needs a hand-written Program.cs (#513).");
        var code = Strip(File.ReadAllText(programPath));

        var velopack = code.IndexOf("VelopackApp.Build()", StringComparison.Ordinal);
        var start = code.IndexOf("Application.Start", StringComparison.Ordinal);
        velopack.ShouldBeGreaterThanOrEqualTo(0, "Program.Main must call VelopackApp.Build()...Run().");
        code.ShouldContain(".Run()");
        start.ShouldBeGreaterThan(velopack, "VelopackApp must run before Application.Start so its hooks can exit before any window exists.");
    }

    [Fact]
    public void The_stripper_ignores_a_call_that_only_appears_in_a_comment()
    {
        Strip("// VelopackApp.Build().Run();\nApplication.Start(null);").ShouldNotContain("VelopackApp");
    }

    private static string Strip(string source)
        => Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), "//.*$", string.Empty, RegexOptions.Multiline);
}
