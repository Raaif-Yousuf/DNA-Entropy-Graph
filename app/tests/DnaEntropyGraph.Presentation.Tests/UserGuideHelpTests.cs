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
    private static ShellViewModel Shell(INavigator? navigator = null, BuildIdentity? build = null)
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return new ShellViewModel(navigator ?? Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center(), TestMessages.Completion(), build: build);
    }

    private static void Showing(INavigator navigator, string key)
    {
        navigator.CurrentPageKey.Returns(key);
        navigator.CurrentPageChanged += Raise.Event();
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

    [Fact]
    public void Back_and_a_deep_link_update_the_help_link_because_they_come_from_the_navigator_not_the_nav_items()
    {
        var navigator = Substitute.For<INavigator>();
        var shell = Shell(navigator);

        shell.NavigateToCommand.Execute("Cloud");
        Showing(navigator, "Results");
        shell.HelpUri.ToString().ShouldEndWith("04-view.md");

        Showing(navigator, "Cloud");
        shell.HelpUri.ToString().ShouldEndWith("06-costs-and-cleanup.md");

        Showing(navigator, "NewRun");
        shell.HelpUri.ToString().ShouldEndWith("03-run.md");
    }

    [Theory]
    [InlineData("RunProgress", "03-run.md")]
    [InlineData("Viewer", "04-view.md")]
    [InlineData("Results", "04-view.md")]
    public void Pages_reached_only_by_navigation_have_a_guide_page(string key, string file)
        => UserGuideLinks.FileFor(key).ShouldBe(file);

    [Fact]
    public void A_signed_release_build_pins_the_guide_to_its_own_tag_and_a_development_build_uses_main()
    {
        var release = new BuildIdentity(BuildInfo.Parse("1.2.3+abcdef0"), isSigned: true);
        var dev = new BuildIdentity(BuildInfo.Parse("1.2.3+abcdef0"), isSigned: false);

        Shell(build: release).HelpUri.ToString().ShouldBe("https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/blob/v1.2.3/docs/user_guide/03-run.md");
        Shell(build: dev).HelpUri.ToString().ShouldBe(UserGuideLinks.BaseUrl + "03-run.md");
        Shell().HelpUri.ToString().ShouldBe(UserGuideLinks.BaseUrl + "03-run.md");
        new BuildIdentity(BuildInfo.Parse(null), isSigned: true).Info.Version.ShouldBe("0.0.0");
        UserGuideLinks.UriFor("Cloud", new BuildIdentity(BuildInfo.Parse(null), isSigned: true)).ToString().ShouldStartWith(UserGuideLinks.BaseUrl);
    }

    [Fact]
    public void The_shell_tells_the_tour_about_the_pane_and_about_framework_closes()
    {
        var tour = new TourViewModel(new TourSettings(Substitute.For<ISettingsStore>()));
        var shell = new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), Substitute.For<IStringResourceProvider>(), TestMessages.Center(), TestMessages.Completion(), tour: tour);

        shell.OnNavigationDisplayModeChanged(ShellViewModel.MinimalNavigationDisplayMode);
        tour.StartCommand.Execute(null);
        tour.IsNewRunStepOpen.ShouldBeFalse();
        shell.OnNavigationDisplayModeChanged(2);
        tour.IsNewRunStepOpen.ShouldBeTrue();

        shell.OnTourTipClosed(TourViewModel.LightDismissReason);
        tour.IsRunning.ShouldBeFalse();
    }
}
