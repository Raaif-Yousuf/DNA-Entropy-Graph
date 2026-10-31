using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #260, wired-to-nothing: the project switcher card is only real if the XAML binds the view model's list, current project, Switch command and status,
/// the page loads the list when it opens (a combo that is never filled shows nothing), and the app registers what the card needs.
/// </summary>
public class ProjectSwitchWiringTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", .. parts]));

    [Theory]
    [InlineData("ItemsSource=\"{x:Bind ProjectSwitch.ProjectIds}\"")]
    [InlineData("SelectedItem=\"{x:Bind ProjectSwitch.ChosenProjectId, Mode=TwoWay}\"")]
    [InlineData("Command=\"{x:Bind ProjectSwitch.SwitchCommand}\"")]
    [InlineData("Text=\"{x:Bind ProjectSwitch.CurrentProjectId, Mode=OneWay}\"")]
    [InlineData("Text=\"{x:Bind ProjectSwitch.BucketText, Mode=OneWay}\"")]
    [InlineData("Text=\"{x:Bind ProjectSwitch.Status, Mode=OneWay}\"")]
    public void The_settings_page_binds_the_switcher_card(string binding)
    {
        Read("Views", "SettingsPage.xaml").ShouldContain(binding);
    }

    [Fact]
    public void The_page_fills_the_list_every_time_it_opens_and_resolves_the_view_model()
    {
        var code = Read("Views", "SettingsPage.xaml.cs");

        code.ShouldContain("GetRequiredService<ProjectSwitchSettingsViewModel>()");
        code.ShouldContain("ProjectSwitch.RefreshCommand.ExecuteAsync(null)");
    }

    [Fact]
    public void The_app_registers_the_store_the_switcher_the_view_model_and_hands_the_store_to_the_reconciler()
    {
        var registration = Read("Startup", "ServiceRegistration.cs");

        registration.ShouldContain("AddSingleton<IProjectSettingsStore>");
        registration.ShouldContain("AddSingleton<ProjectSwitcher>");
        registration.ShouldContain("AddTransient<ProjectSwitchSettingsViewModel>");
        registration.ShouldContain("projectSettings: sp.GetRequiredService<IProjectSettingsStore>()", Case.Sensitive, "the reconciler remembers a resumed run's bucket only if it is given the store");
    }

    [Theory]
    [InlineData("SettingsProjectHeader.Text")]
    [InlineData("SettingsProjectExplainer.Text")]
    [InlineData("ProjectSwitchBox.Header")]
    [InlineData("ProjectSwitchButton.Content")]
    [InlineData("ProjectCurrentLabel.Text")]
    [InlineData("ProjectSwitched")]
    [InlineData("ProjectBucketKnown")]
    [InlineData("ProjectBucketNone")]
    [InlineData("ProjectListFailed")]
    [InlineData("ProjectSwitchFailed")]
    [InlineData("AuthError_PROJECT_UNREACHABLE")]
    [InlineData("AuthAction_ChooseAnotherProject")]
    public void Every_string_the_card_shows_is_in_the_resources_and_has_no_em_dash(string key)
    {
        var resw = Read("Strings", "en-US", "Resources.resw");
        var start = resw.IndexOf($"<data name=\"{key}\"", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, key + " is missing from Resources.resw");
        var value = resw[start..resw.IndexOf("</data>", start, StringComparison.Ordinal)];

        value.ShouldNotContain("—");
    }
}
