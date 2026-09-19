using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #66. Three CLAUDE.md-load-bearing behaviours drive these tests
/// directly: "RUNNING is not working" (the stage title must come from the
/// worker-shaped phase, never imply progress the heartbeat has not
/// confirmed), "long operations never block the UI thread" (every update
/// goes through the fake, synchronous <see cref="IDispatcher"/> so this
/// runs with no real dispatcher queue), and Hard Rule 13 (no literal copy -
/// every title comes from <see cref="IStringResourceProvider"/>).
/// </summary>
public class RunProgressViewModelTests
{
    private sealed class ImmediateDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private static RunProgressViewModel CreateViewModel(
        out IJobEngine jobEngine,
        out IDialogService dialogService,
        out IRunVmActions vmActions,
        out ILogTailReader logTailReader,
        out IMessenger messenger)
    {
        jobEngine = Substitute.For<IJobEngine>();
        dialogService = Substitute.For<IDialogService>();
        vmActions = Substitute.For<IRunVmActions>();
        logTailReader = Substitute.For<ILogTailReader>();
        messenger = new WeakReferenceMessenger();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());

        return new RunProgressViewModel(jobEngine, new ImmediateDispatcher(), dialogService, vmActions, logTailReader, strings, messenger);
    }

    [Fact]
    public void The_stage_title_resolves_from_the_string_resource_provider_for_the_current_phase()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("PhaseRunning_Title").Returns("Analysing");
        var messenger = new WeakReferenceMessenger();
        var viewModel = new RunProgressViewModel(
            Substitute.For<IJobEngine>(), new ImmediateDispatcher(), Substitute.For<IDialogService>(),
            Substitute.For<IRunVmActions>(), Substitute.For<ILogTailReader>(), strings, messenger)
        {
            JobId = "job-1",
        };

        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));

        viewModel.StageTitle.ShouldBe("Analysing");
        viewModel.CurrentPhase.ShouldBe(JobPhase.Running);
    }

    [Fact]
    public void A_phase_message_for_a_different_job_id_is_ignored()
    {
        var viewModel = CreateViewModel(out _, out _, out _, out _, out var messenger);
        viewModel.JobId = "job-1";
        var before = viewModel.CurrentPhase;

        messenger.Send(new RunPhaseChangedMessage("some-other-job", JobPhase.Running));

        viewModel.CurrentPhase.ShouldBe(before);
    }

    [Theory]
    [InlineData(JobPhase.Draft, false)]
    [InlineData(JobPhase.Validating, false)]
    [InlineData(JobPhase.Uploading, false)]
    [InlineData(JobPhase.Provisioning, true)]
    [InlineData(JobPhase.Preparing, true)]
    [InlineData(JobPhase.Running, true)]
    [InlineData(JobPhase.Finalizing, true)]
    [InlineData(JobPhase.Downloading, false)]
    [InlineData(JobPhase.Completed, false)]
    [InlineData(JobPhase.Cancelled, false)]
    public void Stop_and_delete_VM_are_only_actionable_while_a_VM_plausibly_exists(JobPhase phase, bool expectedActionable)
    {
        // docs/ui_conventions.md section 7's own table: Stop/Delete VM are
        // enabled exactly for Provisioning/Preparing/Running/Finalizing -
        // never before a VM could exist, never after its lifecycle is
        // already settled. This is "RUNNING is not working" from the other
        // direction: the buttons must not imply a VM exists when the phase
        // says otherwise.
        var viewModel = CreateViewModel(out _, out _, out _, out _, out var messenger);
        viewModel.JobId = "job-1";

        messenger.Send(new RunPhaseChangedMessage("job-1", phase));

        viewModel.IsVmActionable.ShouldBe(expectedActionable);
        viewModel.StopVmCommand.CanExecute(null).ShouldBe(expectedActionable);
        viewModel.DeleteVmCommand.CanExecute(null).ShouldBe(expectedActionable);
    }

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.PartiallyCompleted)]
    [InlineData(JobPhase.Cancelled)]
    [InlineData(JobPhase.Failed)]
    public void A_terminal_phase_disables_cancel_and_hides_the_stay_open_banner(JobPhase terminalPhase)
    {
        var viewModel = CreateViewModel(out _, out _, out _, out _, out var messenger);
        viewModel.JobId = "job-1";
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));

        messenger.Send(new RunPhaseChangedMessage("job-1", terminalPhase));

        viewModel.CanCancel.ShouldBeFalse();
        viewModel.CancelCommand.CanExecute(null).ShouldBeFalse();
        viewModel.IsStayOpenBannerVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_calls_the_job_engine_with_the_current_job_id()
    {
        var viewModel = CreateViewModel(out var jobEngine, out _, out _, out _, out var messenger);
        viewModel.JobId = "job-1";
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));

        await viewModel.CancelCommand.ExecuteAsync(null);

        await jobEngine.Received(1).CancelRunAsync("job-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stopping_the_VM_asks_for_confirmation_before_calling_the_gateway()
    {
        var viewModel = CreateViewModel(out _, out var dialogService, out var vmActions, out _, out var messenger);
        viewModel.JobId = "job-1";
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));
        dialogService.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        await viewModel.StopVmCommand.ExecuteAsync(null);

        await vmActions.Received(1).StopVmAsync("job-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_stop_confirmation_never_calls_the_gateway()
    {
        // The mutation this guards against: a StopVmCommand that calls the
        // gateway unconditionally would still pass the test above, since
        // that test only ever confirms. This is the one that actually
        // proves the dialog gates the action rather than merely appearing.
        var viewModel = CreateViewModel(out _, out var dialogService, out var vmActions, out _, out var messenger);
        viewModel.JobId = "job-1";
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));
        dialogService.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        await viewModel.StopVmCommand.ExecuteAsync(null);

        await vmActions.DidNotReceive().StopVmAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_delete_confirmation_never_calls_the_gateway()
    {
        var viewModel = CreateViewModel(out _, out var dialogService, out var vmActions, out _, out var messenger);
        viewModel.JobId = "job-1";
        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));
        dialogService.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        await viewModel.DeleteVmCommand.ExecuteAsync(null);

        await vmActions.DidNotReceive().DeleteVmAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_progress_message_for_this_job_updates_the_fraction_and_detail_text()
    {
        var viewModel = CreateViewModel(out _, out _, out _, out _, out var messenger);
        viewModel.JobId = "job-1";

        messenger.Send(new RunProgressChangedMessage("job-1", new ProgressEvent("running", 0.42, "SetTnpB - window 4 of 11, forward")));

        viewModel.FractionComplete.ShouldBe(0.42);
        viewModel.StatusDetailText.ShouldBe("SetTnpB - window 4 of 11, forward");
    }

    [Fact]
    public void A_progress_message_for_a_different_job_id_is_ignored()
    {
        var viewModel = CreateViewModel(out _, out _, out _, out _, out var messenger);
        viewModel.JobId = "job-1";

        messenger.Send(new RunProgressChangedMessage("some-other-job", new ProgressEvent("running", 0.99, "should not apply")));

        viewModel.FractionComplete.ShouldBe(0);
        viewModel.StatusDetailText.ShouldBeNull();
    }

    [Fact]
    public void Setting_the_job_id_refreshes_the_log_tail_from_the_reader()
    {
        var logTailReader = Substitute.For<ILogTailReader>();
        logTailReader.ReadLines("job-1").Returns(new[] { "line one", "line two" });
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());
        var viewModel = new RunProgressViewModel(
            Substitute.For<IJobEngine>(), new ImmediateDispatcher(), Substitute.For<IDialogService>(),
            Substitute.For<IRunVmActions>(), logTailReader, strings, new WeakReferenceMessenger());

        viewModel.JobId = "job-1";

        viewModel.LogTailText.ShouldBe($"line one{Environment.NewLine}line two");
    }
}
