using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #62's own observable: "Launching the app on a fresh profile shows
/// the shell with four destinations and the pill reads 'Not signed in'."
/// The badge half is the other observable named in the lane brief: a run
/// started via IJobEngine (App-layer JobEngine publishes
/// <see cref="RunPhaseChangedMessage"/> over the same messenger,
/// docs/architecture.md section 3) must flip <see cref="ShellViewModel.HasActiveRun"/>
/// with no direct reference from Shell back to JobEngine.
/// </summary>
public class ShellViewModelTests
{
    private static IStringResourceProvider CreatePassthroughStrings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());
        return strings;
    }

    private static ShellViewModel CreateViewModel(out INavigator navigator, out IGcpAccount gcpAccount, out IMessenger messenger)
    {
        navigator = Substitute.For<INavigator>();
        gcpAccount = Substitute.For<IGcpAccount>();
        messenger = new WeakReferenceMessenger();
        return new ShellViewModel(navigator, gcpAccount, messenger, CreatePassthroughStrings(), TestMessages.Center());
    }

    [Fact]
    public void The_status_pill_reads_Not_signed_in_when_no_account_is_signed_in()
    {
        var navigator = Substitute.For<INavigator>();
        var gcpAccount = Substitute.For<IGcpAccount>();
        gcpAccount.IsSignedIn.Returns(false);
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("StatusPillNotSignedIn").Returns("Not signed in");
        var viewModel = new ShellViewModel(navigator, gcpAccount, new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.StatusPillText.ShouldBe("Not signed in");
    }

    [Fact]
    public void The_status_pill_names_the_project_once_signed_in()
    {
        var navigator = Substitute.For<INavigator>();
        var gcpAccount = Substitute.For<IGcpAccount>();
        gcpAccount.IsSignedIn.Returns(true);
        gcpAccount.SelectedProjectId.Returns("deg-123-abc");
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("StatusPillSignedIn").Returns("Signed in - {0}");
        var viewModel = new ShellViewModel(navigator, gcpAccount, new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.StatusPillText.ShouldBe("Signed in - deg-123-abc");
    }

    [Fact]
    public void The_status_pill_resolves_its_text_from_the_string_resource_provider_not_a_literal()
    {
        // The mutation this guards against: ShellViewModel hardcoding
        // "Not signed in" inline (Hard Rule 13) would still pass the two
        // tests above, since they only assert the final text, not where it
        // came from - this is the one that actually distinguishes them.
        var navigator = Substitute.For<INavigator>();
        var gcpAccount = Substitute.For<IGcpAccount>();
        gcpAccount.IsSignedIn.Returns(false);
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("StatusPillNotSignedIn").Returns("(from resw) Not signed in");
        var viewModel = new ShellViewModel(navigator, gcpAccount, new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.StatusPillText.ShouldBe("(from resw) Not signed in");
        strings.Received(1).GetString("StatusPillNotSignedIn");
    }

    // Issue #490: MEASURED 2026-10-02, the header showed the raw key "NewRun".
    [Fact]
    public void The_header_title_is_the_localized_string_for_the_current_page_not_the_page_key()
    {
        var navigator = Substitute.For<INavigator>();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("PageTitle_NewRun").Returns("New run");
        strings.GetString("PageTitle_Cloud").Returns("Cloud services");
        var viewModel = new ShellViewModel(navigator, Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.CurrentPageTitle.ShouldBe("New run");
        viewModel.NavigateToCommand.Execute("Cloud");
        viewModel.CurrentPageTitle.ShouldBe("Cloud services");
    }

    [Fact]
    public void Changing_the_page_raises_PropertyChanged_for_the_header_title()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.NavigateToCommand.Execute("Runs");

        raised.ShouldContain("CurrentPageTitle");
    }

    [Fact]
    public void The_window_title_comes_from_the_string_resource_provider()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("AppDisplayName").Returns("DNA Entropy Graph");
        var viewModel = new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.WindowTitle.ShouldBe("DNA Entropy Graph");
    }

    [Fact]
    public void The_shell_starts_on_New_run_and_exposes_that_key_as_the_initial_destination()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("PageTitle_NewRun").Returns("New run");
        var viewModel = new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.CurrentPageTitle.ShouldBe("New run");
        ShellViewModel.InitialPageKey.ShouldBe("NewRun");
    }

    [Fact]
    public void NavigateTo_forwards_to_the_navigator_and_updates_the_header()
    {
        var navigator = Substitute.For<INavigator>();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("PageTitle_Cloud").Returns("Cloud");
        var viewModel = new ShellViewModel(navigator, Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.NavigateToCommand.Execute("Cloud");

        navigator.Received(1).NavigateTo("Cloud", Arg.Any<object?>());
        viewModel.CurrentPageTitle.ShouldBe("Cloud");
    }

    // The resw keys are looked up by literal name (scripts/check_app_wiring.py's ORPHAN-RESOURCE
    // scan sees only literal keys), so every nav destination's key is named here once.
    [Theory]
    [InlineData("NewRun", "PageTitle_NewRun")]
    [InlineData("Runs", "PageTitle_Runs")]
    [InlineData("Cloud", "PageTitle_Cloud")]
    [InlineData("Settings", "PageTitle_Settings")]
    public void Each_nav_destination_maps_to_its_own_literal_title_key(string pageKey, string reswKey)
        => ShellViewModel.PageTitleKey(pageKey).ShouldBe(reswKey);

    [Fact]
    public void A_page_with_no_title_key_shows_an_empty_header_never_a_raw_key()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var viewModel = new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center());

        viewModel.NavigateToCommand.Execute("SomethingUnbuilt");

        viewModel.CurrentPageTitle.ShouldBe(string.Empty);
    }

    [Fact]
    public void HasActiveRun_is_false_before_any_run_starts()
    {
        var viewModel = CreateViewModel(out _, out _, out _);

        viewModel.HasActiveRun.ShouldBeFalse();
    }

    [Fact]
    public void HasActiveRun_becomes_true_when_a_run_reaches_a_non_terminal_phase()
    {
        var viewModel = CreateViewModel(out _, out _, out var messenger);

        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Validating));

        viewModel.HasActiveRun.ShouldBeTrue();
    }

    [Fact]
    public void HasActiveRun_becomes_false_once_that_jobs_phase_reaches_a_terminal_state()
    {
        var viewModel = CreateViewModel(out _, out _, out var messenger);
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));

        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Completed));

        viewModel.HasActiveRun.ShouldBeFalse();
    }

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.PartiallyCompleted)]
    [InlineData(JobPhase.Cancelled)]
    [InlineData(JobPhase.Failed)]
    public void Every_terminal_phase_clears_the_badge(JobPhase terminalPhase)
    {
        var viewModel = CreateViewModel(out _, out _, out var messenger);
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));

        messenger.Send(new RunPhaseChangedMessage("job-1", terminalPhase));

        viewModel.HasActiveRun.ShouldBeFalse();
    }

    [Fact]
    public void HasActiveRun_stays_true_while_a_second_job_is_still_running_after_the_first_completes()
    {
        var viewModel = CreateViewModel(out _, out _, out var messenger);
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));
        messenger.Send(new RunPhaseChangedMessage("job-2", JobPhase.Provisioning));

        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Completed));

        viewModel.HasActiveRun.ShouldBeTrue();
    }

    [Fact]
    public void The_shell_exposes_the_message_center_it_was_given()
    {
        var messages = TestMessages.Center();
        var viewModel = new ShellViewModel(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), CreatePassthroughStrings(), messages);

        viewModel.Messages.ShouldBeSameAs(messages);
    }
}
