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
        return new ShellViewModel(navigator, gcpAccount, messenger, CreatePassthroughStrings());
    }

    [Fact]
    public void The_status_pill_reads_Not_signed_in_when_no_account_is_signed_in()
    {
        var navigator = Substitute.For<INavigator>();
        var gcpAccount = Substitute.For<IGcpAccount>();
        gcpAccount.IsSignedIn.Returns(false);
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("StatusPillNotSignedIn").Returns("Not signed in");
        var viewModel = new ShellViewModel(navigator, gcpAccount, new WeakReferenceMessenger(), strings);

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
        var viewModel = new ShellViewModel(navigator, gcpAccount, new WeakReferenceMessenger(), strings);

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
        var viewModel = new ShellViewModel(navigator, gcpAccount, new WeakReferenceMessenger(), strings);

        viewModel.StatusPillText.ShouldBe("(from resw) Not signed in");
        strings.Received(1).GetString("StatusPillNotSignedIn");
    }

    [Fact]
    public void NavigateTo_forwards_to_the_navigator_and_updates_the_current_page_key()
    {
        var viewModel = CreateViewModel(out var navigator, out _, out _);

        viewModel.NavigateToCommand.Execute("Cloud");

        navigator.Received(1).NavigateTo("Cloud", Arg.Any<object?>());
        viewModel.CurrentPageKey.ShouldBe("Cloud");
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
}
