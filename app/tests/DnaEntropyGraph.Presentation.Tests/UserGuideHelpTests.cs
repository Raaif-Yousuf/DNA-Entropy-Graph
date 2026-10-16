using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #143: the help button opens the user guide page for the page the user is on.</summary>
public sealed class UserGuideHelpTests
{
    private static ShellViewModel Shell()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center(), TestMessages.Completion());
    }

    [Theory]
    [InlineData("NewRun", "03-run.md")]
    [InlineData("Runs", "05-history.md")]
    [InlineData("Cloud", "06-costs-and-cleanup.md")]
    [InlineData("Settings", "08-settings.md")]
    [InlineData("Wizard", "02-connect-google-cloud.md")]
    public void Navigating_to_a_page_points_the_help_link_at_its_guide_page(string pageKey, string guideFile)
    {
        var shell = Shell();

        shell.NavigateToCommand.Execute(pageKey);

        shell.HelpUri.ToString().ShouldBe(UserGuideLinks.BaseUrl + guideFile);
    }

    [Fact]
    public void The_help_link_starts_on_the_New_run_page_and_an_unknown_page_falls_back_to_the_guide_index()
    {
        var shell = Shell();

        shell.HelpUri.ToString().ShouldEndWith("03-run.md");
        shell.NavigateToCommand.Execute("SomethingNew");
        shell.HelpUri.ToString().ShouldBe(UserGuideLinks.BaseUrl + "README.md");
    }

    [Fact]
    public void The_help_link_change_is_announced_so_the_bound_button_updates()
    {
        var shell = Shell();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.NavigateToCommand.Execute("Cloud");

        changed.ShouldContain(nameof(ShellViewModel.HelpUri));
    }

    [Fact]
    public void The_shell_exposes_the_tour_it_was_given()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        var tour = new TourViewModel(new TourSettings(Substitute.For<DnaEntropyGraph.Core.Abstractions.ISettingsStore>()));

        var shell = new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center(), TestMessages.Completion(), tour: tour);

        shell.Tour.ShouldBeSameAs(tour);
    }
}
