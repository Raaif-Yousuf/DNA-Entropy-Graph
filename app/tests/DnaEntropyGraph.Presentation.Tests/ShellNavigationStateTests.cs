using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issues #499, #701 and #807: the header and the selected navigation item follow the page the frame really shows
/// (<see cref="INavigator.CurrentPageChanged"/>), whoever navigated: a nav item, a page's command, Back or a deep link.
/// </summary>
public class ShellNavigationStateTests
{
    private static ShellViewModel Create(INavigator navigator)
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return new ShellViewModel(navigator, Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center(), TestMessages.Completion());
    }

    private static void Show(INavigator navigator, string key)
    {
        navigator.CurrentPageKey.Returns(key);
        navigator.CurrentPageChanged += Raise.Event();
    }

    [Theory]
    [InlineData("RunProgress", "PageTitle_RunProgress")]
    [InlineData("Viewer", "PageTitle_Viewer")]
    [InlineData("Results", "PageTitle_Results")]
    [InlineData("Runs", "PageTitle_Runs")]
    public void A_page_that_navigates_the_frame_itself_sets_the_header(string key, string expectedTitle)
    {
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Show(navigator, key);

        viewModel.CurrentPageTitle.ShouldBe(expectedTitle);
        changed.ShouldContain(nameof(ShellViewModel.CurrentPageTitle));
        changed.ShouldContain(nameof(ShellViewModel.SelectedNavKey));
    }

    [Theory]
    [InlineData("NewRun")]
    [InlineData("Runs")]
    [InlineData("Cloud")]
    [InlineData("Settings")]
    public void A_page_with_a_nav_item_selects_that_item(string key)
    {
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);

        Show(navigator, key);

        viewModel.SelectedNavKey.ShouldBe(key);
    }

    [Theory]
    [InlineData("RunProgress")]
    [InlineData("Viewer")]
    [InlineData("Results")]
    [InlineData("Wizard")]
    public void A_page_with_no_nav_item_clears_the_selection_so_its_parent_can_be_clicked_again(string key)
    {
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);
        Show(navigator, "Runs");

        Show(navigator, key);

        viewModel.SelectedNavKey.ShouldBeNull();
    }

    [Fact]
    public void Back_moves_the_header_and_the_selection_to_the_page_you_return_to()
    {
        var navigator = Substitute.For<INavigator>();
        navigator.CanGoBack.Returns(true);
        navigator.When(n => n.GoBack()).Do(_ => Show(navigator, "Runs"));
        var viewModel = Create(navigator);
        Show(navigator, "RunProgress");
        viewModel.CurrentPageTitle.ShouldBe("PageTitle_RunProgress");

        viewModel.GoBack();

        viewModel.CurrentPageTitle.ShouldBe("PageTitle_Runs");
        viewModel.SelectedNavKey.ShouldBe("Runs");
    }

    [Fact]
    public void A_deep_link_to_a_page_updates_the_header_without_a_nav_click()
    {
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);

        Show(navigator, "Results");

        viewModel.CurrentPageTitle.ShouldBe("PageTitle_Results");
    }

    [Fact]
    public void A_null_current_page_changes_nothing()
    {
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);

        navigator.CurrentPageKey.Returns((string?)null);
        navigator.CurrentPageChanged += Raise.Event();

        viewModel.CurrentPageTitle.ShouldBe("PageTitle_NewRun");
        viewModel.SelectedNavKey.ShouldBe("NewRun");
    }

    [Fact]
    public void Selecting_the_item_the_frame_already_shows_does_not_navigate_again()
    {
        // The window sets the selected item from SelectedNavKey; that selection raises the nav command, which must not push a duplicate page.
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);
        Show(navigator, "Runs");

        viewModel.NavigateToCommand.Execute("Runs");

        navigator.DidNotReceive().NavigateTo(Arg.Any<string>(), Arg.Any<object?>());
    }

    [Fact]
    public void Clearing_the_selection_does_not_navigate()
    {
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);

        viewModel.NavigateToCommand.Execute(null);

        navigator.DidNotReceive().NavigateTo(Arg.Any<string>(), Arg.Any<object?>());
    }

    [Fact]
    public void The_recording_navigator_passes_the_inner_navigators_page_and_event_through()
    {
        // DI hands the shell the RecordingNavigator decorator, not NavigationService: a decorator that swallowed the event would leave the header stale.
        var inner = Substitute.For<INavigator>();
        var navigator = new RecordingNavigator(inner, new SessionStore(Substitute.For<ISettingsStore>()));
        var viewModel = Create(navigator);

        inner.CurrentPageKey.Returns("Viewer");
        inner.CurrentPageChanged += Raise.Event();

        navigator.CurrentPageKey.ShouldBe("Viewer");
        viewModel.CurrentPageTitle.ShouldBe("PageTitle_Viewer");
    }

    [Fact]
    public void A_nav_click_still_navigates_and_sets_the_header_before_the_navigator_reports()
    {
        var navigator = Substitute.For<INavigator>();
        var viewModel = Create(navigator);

        viewModel.NavigateToCommand.Execute("Cloud");

        navigator.Received(1).NavigateTo("Cloud", Arg.Any<object?>());
        viewModel.CurrentPageTitle.ShouldBe("PageTitle_Cloud");
    }
}
