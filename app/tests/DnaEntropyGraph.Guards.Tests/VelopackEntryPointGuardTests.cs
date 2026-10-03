using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        DisablesGeneratedMainUnconditionally(csproj).ShouldBeTrue(
            "DISABLE_XAML_GENERATED_MAIN must be in a DefineConstants with no Condition (on it or its PropertyGroup), or the generated Main wins in some configuration.");

        var programPath = Path.Combine(AppDir, "Program.cs");
        File.Exists(programPath).ShouldBeTrue("the App needs a hand-written Program.cs (#513).");
        var code = Strip(File.ReadAllText(programPath));

        var run = VelopackRun.Match(code);
        run.Success.ShouldBeTrue("Program.Main must call VelopackApp.Build()...Run() as one chain.");
        var start = code.IndexOf("Application.Start", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(run.Index, "VelopackApp must run before Application.Start so its hooks can exit before any window exists.");
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><DefineConstants Condition=\"'$(Configuration)'=='Debug'\">DISABLE_XAML_GENERATED_MAIN</DefineConstants></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup Condition=\"'$(X)'=='1'\"><DefineConstants>DISABLE_XAML_GENERATED_MAIN</DefineConstants></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><DefineConstants>OTHER</DefineConstants></PropertyGroup></Project>", false)]
    [InlineData("<Project><!-- DISABLE_XAML_GENERATED_MAIN --><PropertyGroup/></Project>", false)]
    public void The_csproj_check_requires_an_unconditional_define(string csproj, bool expected)
        => DisablesGeneratedMainUnconditionally(csproj).ShouldBe(expected);

    [Theory]
    [InlineData("VelopackApp.Build().Run();", true)]
    [InlineData("VelopackApp.Build().SetAutoApplyOnStartup(false).Run();", true)]
    [InlineData("var b = VelopackApp.Build();\n b.Run();", false)]
    [InlineData("VelopackApp.Build();\n Foo.Run();", false)]
    [InlineData("// VelopackApp.Build().Run();\nApplication.Start(null);", false)]
    public void The_run_must_be_on_the_VelopackApp_Build_chain(string source, bool expected)
        => VelopackRun.IsMatch(Strip(source)).ShouldBe(expected);

    private static readonly Regex VelopackRun = new(@"VelopackApp\s*\.\s*Build\s*\(\s*\)(\s*\.\s*\w+\s*\([^;()]*\))*\s*\.\s*Run\s*\(\s*\)\s*;", RegexOptions.Compiled);

    private static bool DisablesGeneratedMainUnconditionally(string csprojText)
        => XDocument.Parse(csprojText).Descendants("DefineConstants").Any(e =>
            e.Attribute("Condition") is null
            && e.Parent?.Attribute("Condition") is null
            && e.Value.Split(';').Contains("DISABLE_XAML_GENERATED_MAIN", StringComparer.Ordinal));

    private static string Strip(string source)
        => Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), "//.*$", string.Empty, RegexOptions.Multiline);
}
