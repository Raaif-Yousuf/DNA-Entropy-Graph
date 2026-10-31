using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
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

        return new RunProgressViewModel(jobEngine, new ImmediateDispatcher(), dialogService, vmActions, logTailReader, strings, Substitute.For<IRunRepository>(), Substitute.For<INavigator>(), messenger);
    }

    [Fact]
    public void The_stage_title_resolves_from_the_string_resource_provider_for_the_current_phase()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("PhaseRunning_Title").Returns("Analysing");
        var messenger = new WeakReferenceMessenger();
        var viewModel = new RunProgressViewModel(
            Substitute.For<IJobEngine>(), new ImmediateDispatcher(), Substitute.For<IDialogService>(),
            Substitute.For<IRunVmActions>(), Substitute.For<ILogTailReader>(), strings, Substitute.For<IRunRepository>(), Substitute.For<INavigator>(), messenger)
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
            Substitute.For<IRunVmActions>(), logTailReader, strings, Substitute.For<IRunRepository>(), Substitute.For<INavigator>(), new WeakReferenceMessenger());

        viewModel.JobId = "job-1";

        viewModel.LogTailText.ShouldBe($"line one{Environment.NewLine}line two");
    }

    // ---- Issue #830: a run whose Google account is signed out says which account to sign in as, with a button ----

    private sealed class RecordingLog : IDiagnosticsLog
    {
        public List<string> Lines { get; } = [];

        public void Warning(string source, string? jobId, string errorClass) => Lines.Add($"{source}/{errorClass}");
    }

    private sealed class SignInRig
    {
        public SignInRig(IReadOnlyList<AccountInfo>? accounts = null)
        {
            var strings = Substitute.For<IStringResourceProvider>();
            strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
            strings.GetString("RunSignIn_Prompt").Returns("Sign in as {0} to stop this run's computer");
            Account.Accounts.Returns(accounts ?? [new AccountInfo("sub-a", "a@example.org")]);
            Dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
            ViewModel = new RunProgressViewModel(Engine, new ImmediateDispatcher(), Dialogs, VmActions, Substitute.For<ILogTailReader>(), strings, Runs, Substitute.For<INavigator>(), Messenger, Account, Log)
            {
                JobId = "job-1",
            };
            Messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));
        }

        public IGcpAccount Account { get; } = Substitute.For<IGcpAccount>();

        public IRunRepository Runs { get; } = Substitute.For<IRunRepository>();

        public RecordingLog Log { get; } = new();

        public IJobEngine Engine { get; } = Substitute.For<IJobEngine>();

        public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

        public IRunVmActions VmActions { get; } = Substitute.For<IRunVmActions>();

        public WeakReferenceMessenger Messenger { get; } = new();

        public RunProgressViewModel ViewModel { get; }

        public static AccountAuthException SignedOut(string? sub) => new(AuthErrorCodes.SigninExpired, "signed out", accountSub: sub);
    }

    [Fact]
    public async Task Stopping_the_VM_of_a_run_whose_account_is_signed_out_offers_to_sign_in_as_that_account()
    {
        var rig = new SignInRig();
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-a")));

        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeTrue();
        rig.ViewModel.SignInPromptText.ShouldBe("Sign in as a@example.org to stop this run's computer");
    }

    [Fact]
    public async Task Deleting_the_VM_of_a_run_whose_account_is_signed_out_offers_the_same_sign_in()
    {
        var rig = new SignInRig();
        rig.VmActions.DeleteVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-a")));

        await rig.ViewModel.DeleteVmCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeTrue();
        rig.ViewModel.SignInPromptText.ShouldContain("a@example.org");
    }

    [Fact]
    public async Task A_signed_out_account_the_list_no_longer_knows_gets_the_prompt_without_an_email()
    {
        var rig = new SignInRig([]);
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-gone")));

        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeTrue();
        rig.ViewModel.SignInPromptText.ShouldBe("RunSignIn_PromptUnknown");
    }

    [Fact]
    public async Task The_sign_in_button_starts_sign_in_and_clears_the_prompt_when_it_succeeds()
    {
        var rig = new SignInRig();
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-a")));
        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        await rig.ViewModel.SignInCommand.ExecuteAsync(null);

        await rig.Account.Received(1).SignInAsync(Arg.Any<CancellationToken>());
        rig.ViewModel.HasSignInPrompt.ShouldBeFalse();
    }

    [Fact]
    public async Task A_sign_in_that_fails_keeps_the_prompt_so_the_person_can_try_again()
    {
        var rig = new SignInRig();
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-a")));
        rig.Account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(AuthErrorCodes.SigninCancelled)));
        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        await rig.ViewModel.SignInCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stop_that_succeeds_clears_an_earlier_sign_in_prompt()
    {
        var rig = new SignInRig();
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-a")), Task.CompletedTask);
        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeFalse();
    }

    [Fact]
    public async Task A_sign_in_as_somebody_else_keeps_the_prompt_until_the_runs_own_account_is_signed_in()
    {
        // Issue #830 review M2 (e): the button starts a Google sign-in that can end as ANY account; only the run's own account being signed in again answers the prompt.
        var rig = new SignInRig([new AccountInfo("sub-a", "a@example.org", NeedsSignIn: true)]);
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-a")));
        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        await rig.ViewModel.SignInCommand.ExecuteAsync(null);
        rig.ViewModel.HasSignInPrompt.ShouldBeTrue("sub-a is still signed out");
        rig.ViewModel.SignInPromptText.ShouldContain("a@example.org", Case.Sensitive, "the kept prompt still says which account");

        rig.Account.Accounts.Returns([new AccountInfo("sub-a", "a@example.org")]);
        await rig.ViewModel.SignInCommand.ExecuteAsync(null);
        rig.ViewModel.HasSignInPrompt.ShouldBeFalse();
    }

    [Fact]
    public async Task Stopping_the_VM_of_a_run_with_no_history_row_says_so_and_names_the_Cloud_page()
    {
        var rig = new SignInRig();
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(new RunNotFoundException("job-1")));

        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        rig.ViewModel.HasActionNotice.ShouldBeTrue();
        rig.ViewModel.ActionNoticeText.ShouldBe("RunAction_RunNotFound");
        rig.ViewModel.HasSignInPrompt.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_a_run_with_no_history_row_says_so()
    {
        var rig = new SignInRig();
        rig.Engine.CancelRunAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(new RunNotFoundException("job-1")));

        await rig.ViewModel.CancelCommand.ExecuteAsync(null);

        rig.ViewModel.ActionNoticeText.ShouldBe("RunAction_RunNotFound");
    }

    [Fact]
    public async Task A_later_stop_that_succeeds_clears_the_notice()
    {
        var rig = new SignInRig();
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(new RunNotFoundException("job-1")), Task.CompletedTask);
        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);

        rig.ViewModel.HasActionNotice.ShouldBeFalse();
    }

    // ---- Issue #830 review F1, F7a, F7b ----

    [Fact]
    public async Task A_cancel_that_ends_signed_out_shows_the_sign_in_prompt_for_the_runs_own_account()
    {
        var rig = new SignInRig();
        rig.Runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>(
            [new RunRecord("job-1", JobPhase.Failed, DateTimeOffset.UtcNow, ErrorCode: RunErrorCodes.AccountSignedOut, AccountSub: "sub-a")]));

        await rig.ViewModel.CancelCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeTrue("the row's copy points at a Sign in button, so it must be on screen");
        rig.ViewModel.SignInPromptText.ShouldContain("a@example.org", Case.Sensitive, "names the run's own account");
    }

    [Fact]
    public async Task A_cancel_that_ends_cancelled_shows_no_sign_in_prompt()
    {
        var rig = new SignInRig();
        rig.Runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>(
            [new RunRecord("job-1", JobPhase.Cancelled, DateTimeOffset.UtcNow, AccountSub: "sub-a")]));

        await rig.ViewModel.CancelCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeFalse();
    }

    [Fact]
    public async Task A_later_cancel_that_succeeds_clears_the_run_not_found_notice()
    {
        var rig = new SignInRig();
        rig.Engine.CancelRunAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(new RunNotFoundException("job-1")), Task.CompletedTask);
        await rig.ViewModel.CancelCommand.ExecuteAsync(null);
        rig.ViewModel.HasActionNotice.ShouldBeTrue();

        await rig.ViewModel.CancelCommand.ExecuteAsync(null);

        rig.ViewModel.HasActionNotice.ShouldBeFalse();
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task A_sign_in_that_fails_for_a_reason_that_is_not_a_sign_in_answer_does_not_escape_and_says_it_failed(Type failure)
    {
        var rig = new SignInRig();
        rig.VmActions.StopVmAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(SignInRig.SignedOut("sub-a")));
        await rig.ViewModel.StopVmCommand.ExecuteAsync(null);
        rig.Account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException((Exception)Activator.CreateInstance(failure)!));

        await rig.ViewModel.SignInCommand.ExecuteAsync(null);

        rig.ViewModel.HasSignInPrompt.ShouldBeTrue();
        rig.ViewModel.SignInPromptText.ShouldBe("AuthError_SIGNIN_FAILED");
        rig.Log.Lines.ShouldBe([$"run-sign-in/{failure.Name}"], "the class only, never a message");
    }
}
