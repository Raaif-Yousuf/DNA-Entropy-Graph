using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #109 (review L5): the spending-cap box saves whatever the view model is handed. With the hard stop on, a binding that pushed every
/// keystroke would save cap=1 on the way to 10 and refuse runs under it. The box must commit when it loses focus, and the XAML says so
/// instead of leaning on a framework default.
/// </summary>
public sealed class SpendCapBoxCommitTests
{
    [Fact]
    public void The_cap_box_commits_on_LostFocus_not_on_every_keystroke()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Views", "SettingsPage.xaml"));

        var box = Regex.Match(xaml, @"<TextBox\s+x:Uid=""SpendCapBox""[^>]*/>");
        box.Success.ShouldBeTrue("vacuity: the cap box is found in SettingsPage.xaml");
        box.Value.ShouldContain("UpdateSourceTrigger=LostFocus");
        box.Value.ShouldNotContain("UpdateSourceTrigger=PropertyChanged");
    }
}
