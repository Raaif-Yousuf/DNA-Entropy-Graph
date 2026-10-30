using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issues #148 and #222, wired-to-nothing: two things only the XAML can get wrong and no unit test sees. A TwoWay TextBox.Text binding
/// pushes on LostFocus by default, so text typed while the page-open look runs would not reach the view model and would be replaced;
/// and the amount box must be disabled while a save runs.
/// </summary>
public class BudgetWiringTests
{
    private static string Xaml() => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Views", "SettingsPage.xaml"));

    private static string AmountBoxElement()
    {
        var xaml = Xaml();
        var start = xaml.IndexOf("<TextBox x:Uid=\"BudgetAmountBox\"", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1);
        return xaml[start..xaml.IndexOf("/>", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void The_amount_box_pushes_every_keystroke_to_the_view_model()
    {
        AmountBoxElement().ShouldContain("UpdateSourceTrigger=PropertyChanged");
    }

    [Fact]
    public void The_amount_box_is_disabled_while_the_view_model_is_busy()
    {
        AmountBoxElement().ShouldContain("IsEnabled=\"{x:Bind Budget.CanEdit, Mode=OneWay}\"");
    }
}
