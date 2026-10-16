using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #110 (Appendix A section 6): a shortcut or live region that is written in a ViewModel and never put in the XAML does nothing
/// and nothing fails. These read the real XAML for the one line each feature rests on.
/// </summary>
public sealed class KeyboardShortcutWiringTests
{
    private static string Xaml(string name) => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", name.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("MainWindow.xaml", "Key=\"Left\" Modifiers=\"Menu\" Invoked=\"{x:Bind ViewModel.GoBack}\"")]
    [InlineData("MainWindow.xaml", "Key=\"N\" Modifiers=\"Control\"")]
    [InlineData("Views/NewRunPage.xaml", "Key=\"O\" Modifiers=\"Control\"")]
    [InlineData("Views/NewRunPage.xaml", "Key=\"V\" Modifiers=\"Control,Shift\" Invoked=\"{x:Bind ViewModel.PasteFromClipboard}\"")]
    [InlineData("Views/NewRunPage.xaml", "Key=\"Enter\" Modifiers=\"Control\"")]
    [InlineData("Views/RunsPage.xaml", "Key=\"F5\"")]
    public void The_shortcut_is_in_the_XAML(string file, string fragment)
        => Xaml(file).ShouldContain(fragment);

    [Fact]
    public void The_Settings_shortcut_is_added_in_code_behind_because_the_comma_has_no_VirtualKey_name()
        => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "MainWindow.xaml.cs")).ShouldContain("(Windows.System.VirtualKey)188");

    [Fact]
    public void The_run_progress_stage_and_detail_lines_are_live_regions()
    {
        var xaml = Xaml("Views/RunProgressPage.xaml");
        System.Text.RegularExpressions.Regex.Matches(xaml, "AutomationProperties.LiveSetting=\"Polite\"").Count.ShouldBe(2);
        xaml.ShouldContain("ViewModel.StageTitle");
    }

    [Theory]
    [InlineData("NavNewRun.AccessKey")]
    [InlineData("NavRuns.AccessKey")]
    [InlineData("NavCloud.AccessKey")]
    [InlineData("NavSettings.AccessKey")]
    public void Every_nav_item_has_an_access_key(string name)
        => RepoPaths.AllReswFiles.Any(f => File.ReadAllText(f).Contains($"<data name=\"{name}\"")).ShouldBeTrue();
}
