using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The page a biologist stares at for 6 to 20 minutes (issue #66). Every
/// state change arrives over <see cref="IMessenger"/> (docs/architecture.md
/// section 3) and is applied through <see cref="IDispatcher"/> - a real,
/// UI-thread-marshalled update, not a direct field write - because the
/// message can arrive from a background poll (Critical Pitfalls: "long
/// operations never block the UI thread"). <see cref="StageTitle"/> and
/// <see cref="IsVmActionable"/> both key off <see cref="JobPhase"/> alone,
/// never off a VM's own RUNNING status: CLAUDE.md's "RUNNING is not
/// working" - instance status says nothing about the job, so this
/// ViewModel never reads one directly (it has no dependency capable of
/// doing so; only the phase, driven by the worker's own heartbeat once a
/// real runner exists, moves this page).
/// </summary>
public sealed partial class RunProgressViewModel : ObservableObject
{
    private readonly IJobEngine _jobEngine;
    private readonly IDispatcher _dispatcher;
    private readonly IDialogService _dialogService;
    private readonly IRunVmActions _vmActions;
    private readonly ILogTailReader _logTailReader;
    private readonly IStringResourceProvider _strings;
    private readonly IRunRepository _runRepository;
    private readonly INavigator _navigator;
    private readonly IGcpAccount? _account;
    private readonly IDiagnosticsLog _log;

    [ObservableProperty]
    private string? _jobId;

    [ObservableProperty]
    private JobPhase _currentPhase = JobPhase.Draft;

    [ObservableProperty]
    private string _stageTitle = string.Empty;

    [ObservableProperty]
    private string? _statusDetailText;

    [ObservableProperty]
    private double _fractionComplete;

    [ObservableProperty]
    private string _logTailText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVmCommand))]
    private bool _isVmActionable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel = true;

    [ObservableProperty]
    private bool _isStayOpenBannerVisible = true;

    // True only for a Completed or PartiallyCompleted run whose output folder is known (issues #72/#73).
    // Not observable: the button binds OpenViewerCommand, whose CanExecute reads this.
    private bool _canOpenViewer;

    private string? _outputFolder;

    public RunProgressViewModel(
        IJobEngine jobEngine,
        IDispatcher dispatcher,
        IDialogService dialogService,
        IRunVmActions vmActions,
        ILogTailReader logTailReader,
        IStringResourceProvider strings,
        IRunRepository runRepository,
        INavigator navigator,
        IMessenger messenger,
        IGcpAccount? account = null,
        IDiagnosticsLog? log = null)
    {
        _account = account;
        _log = log ?? NullDiagnosticsLog.Instance;
        _jobEngine = jobEngine;
        _dispatcher = dispatcher;
        _dialogService = dialogService;
        _vmActions = vmActions;
        _logTailReader = logTailReader;
        _strings = strings;
        _runRepository = runRepository;
        _navigator = navigator;
        ApplyPhase(JobPhase.Draft);

        messenger.Register<RunProgressViewModel, RunPhaseChangedMessage>(this, static (recipient, message) => recipient.OnRunPhaseChanged(message));
        messenger.Register<RunProgressViewModel, RunProgressChangedMessage>(this, static (recipient, message) => recipient.OnRunProgressChanged(message));
        messenger.Register<RunProgressViewModel, RunNoticeMessage>(this, static (recipient, message) => recipient.OnRunNotice(message));
    }

    /// <summary>
    /// Issue #830: set when this run's Google account is signed out and a stop or delete of its computer could not be sent: "Sign in as (the account) to stop this run's computer".
    /// Empty when there is nothing to sign in to. The account named is the run's own, never the one that happens to be current.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInPrompt))]
    private string _signInPromptText = string.Empty;

    public bool HasSignInPrompt => SignInPromptText.Length > 0;

    /// <summary>The account the sign-in prompt asks for (its sub, never the email); null when the prompt is not showing or the account is not known.</summary>
    private string? _promptSub;

    /// <summary>Issue #830 review M5: set when Stop, Delete or Cancel found no history row for this run (the app cannot say where its computer is), naming the Cloud page. Empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionNotice))]
    private string _actionNoticeText = string.Empty;

    public bool HasActionNotice => ActionNoticeText.Length > 0;

    /// <summary>The prompt's button: starts Google sign-in; on success the prompt goes away and the person presses Stop or Delete again. A failed or cancelled sign-in keeps the prompt.</summary>
    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        try
        {
            await _account.SignInAsync(cancellationToken).ConfigureAwait(false);

            // The sign-in can end as ANY Google account: the prompt goes only when the run's own account is signed in again (an unknown account cannot be checked, so any sign-in answers it).
            var promptSub = _promptSub;
            if (promptSub is null || _account.Accounts.Any(a => a.Sub == promptSub && !a.NeedsSignIn))
            {
                SetSignInPrompt(string.Empty);
            }
        }
        catch (AccountAuthException)
        {
            // The sign-in screen said why (cancelled, timed out); the prompt stays so the person can try again.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Review F7a: a cancelled browser wait or a network error is not an AccountAuthException and must not escape the command. Log the class only; the prompt (with its button) now says the sign-in failed.
            _log.Warning("run-sign-in", JobId, ex.GetType().Name);
            SetSignInPrompt(_strings.GetString("AuthError_SIGNIN_FAILED"));
        }
    }

    /// <summary>Runs a stop or delete of this run's computer; a signed-out account becomes the sign-in prompt instead of an unhandled exception.</summary>
    private async Task RunVmActionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            SetSignInPrompt(string.Empty);
            SetActionNotice(string.Empty);
        }
        catch (RunNotFoundException)
        {
            SetSignInPrompt(string.Empty);
            SetActionNotice(_strings.GetString("RunAction_RunNotFound"));
        }
        catch (AccountAuthException ex) when (ex.Code == AuthErrorCodes.SigninExpired)
        {
            ShowSignInPrompt(ex.AccountSub);
        }
    }

    /// <summary>The prompt for the run's own account (never the current one): "Sign in as (email)", or the no-email wording when the list no longer knows the account.</summary>
    private void ShowSignInPrompt(string? accountSub)
    {
        _promptSub = accountSub;
        var email = _account?.Accounts.FirstOrDefault(a => a.Sub == accountSub)?.Email;
        SetSignInPrompt(email is null
            ? _strings.GetString("RunSignIn_PromptUnknown")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("RunSignIn_Prompt"), email));
    }

    private void SetSignInPrompt(string text) => _dispatcher.Enqueue(() => SignInPromptText = text);

    private void SetActionNotice(string text) => _dispatcher.Enqueue(() => ActionNoticeText = text);

    /// <summary>The "taking longer than usual" or "looks stuck" line for this run (issue #675); empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSlowNotice))]
    private string _slowNoticeText = string.Empty;

    public bool HasSlowNotice => SlowNoticeText.Length > 0;

    private void OnRunNotice(RunNoticeMessage message)
    {
        if (message.JobId != JobId)
        {
            return;
        }

        _dispatcher.Enqueue(() => SlowNoticeText = message.Text);
    }

    partial void OnJobIdChanged(string? value)
    {
        RefreshLogTail();
        _outputFolder = null;
        UpdateCanOpenViewer();
    }

    private void OnRunPhaseChanged(RunPhaseChangedMessage message)
    {
        if (message.JobId != JobId)
        {
            return;
        }

        _dispatcher.Enqueue(() => ApplyPhase(message.Phase));
    }

    private void OnRunProgressChanged(RunProgressChangedMessage message)
    {
        if (message.JobId != JobId)
        {
            return;
        }

        _dispatcher.Enqueue(() =>
        {
            FractionComplete = message.Progress.FractionComplete;
            StatusDetailText = message.Progress.Message;
        });
    }

    private void ApplyPhase(JobPhase phase)
    {
        CurrentPhase = phase;
        StageTitle = _strings.GetString(PhaseTitleKey(phase));
        IsVmActionable = phase is JobPhase.Provisioning or JobPhase.Preparing or JobPhase.Running or JobPhase.Finalizing;
        CanCancel = !IsTerminal(phase) && phase != JobPhase.Cancelling;
        IsStayOpenBannerVisible = !IsTerminal(phase);
        if (IsTerminal(phase))
        {
            // A finished run is no longer slow.
            SlowNoticeText = string.Empty;
        }

        _outputFolder = null;
        UpdateCanOpenViewer();
        if (phase is JobPhase.Completed or JobPhase.PartiallyCompleted)
        {
            _ = LoadOutputFolderAsync(phase);
        }
    }

    // The folder the finished run's results were downloaded to (RunRecord.OutputDir, written by the runner).
    private async Task LoadOutputFolderAsync(JobPhase phase)
    {
        try
        {
            var runs = await _runRepository.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
            var folder = runs.FirstOrDefault(r => r.JobId == JobId)?.OutputDir;
            _dispatcher.Enqueue(() =>
            {
                if (CurrentPhase == phase)
                {
                    _outputFolder = folder;
                    UpdateCanOpenViewer();
                }
            });
        }
        catch (Exception)
        {
            // No folder known: Open viewer stays disabled rather than failing the page.
        }
    }

    private void UpdateCanOpenViewer()
    {
        _canOpenViewer = CurrentPhase is JobPhase.Completed or JobPhase.PartiallyCompleted && !string.IsNullOrEmpty(_outputFolder);
        OpenViewerCommand.NotifyCanExecuteChanged();
        OpenResultsCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenViewer() => _canOpenViewer;

    [RelayCommand(CanExecute = nameof(CanOpenViewer))]
    private void OpenViewer() => _navigator.NavigateTo(ViewerViewModel.PageKey, _outputFolder);

    // The Results page (issue #102) says for itself when the folder is gone, so this needs only a finished run.
    private bool CanOpenResults() => CurrentPhase is JobPhase.Completed or JobPhase.PartiallyCompleted;

    [RelayCommand(CanExecute = nameof(CanOpenResults))]
    private void OpenResults() => _navigator.NavigateTo(ResultsViewModel.PageKey, JobId);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (JobId is null)
        {
            return;
        }

        try
        {
            await _jobEngine.CancelRunAsync(JobId, cancellationToken).ConfigureAwait(false);
            SetActionNotice(string.Empty);
        }
        catch (RunNotFoundException)
        {
            SetActionNotice(_strings.GetString("RunAction_RunNotFound"));
            return;
        }

        // Review F1: a cancel whose account is signed out does not throw: the row ends Failed with account_signed_out, whose copy points at a Sign in button. Show it here.
        await ShowSignInPromptIfCancelEndedSignedOutAsync().ConfigureAwait(false);
    }

    private async Task ShowSignInPromptIfCancelEndedSignedOutAsync()
    {
        try
        {
            var rows = await _runRepository.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
            if (rows.LastOrDefault(r => r.JobId == JobId) is { Phase: JobPhase.Failed, ErrorCode: RunErrorCodes.AccountSignedOut } row)
            {
                ShowSignInPrompt(row.AccountSub);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("run-cancel", JobId, ex.GetType().Name);
        }
    }

    [RelayCommand(CanExecute = nameof(IsVmActionable))]
    private async Task StopVmAsync(CancellationToken cancellationToken)
    {
        if (JobId is null)
        {
            return;
        }

        // Plain (non-dotted) resw keys: see ShellViewModel.BuildStatusPillText's comment.
        var confirmed = await _dialogService.ConfirmAsync(
            _strings.GetString("ConfirmStopVm_Title"),
            _strings.GetString("ConfirmStopVm_Body"),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed)
        {
            return;
        }

        await RunVmActionAsync(() => _vmActions.StopVmAsync(JobId, cancellationToken)).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(IsVmActionable))]
    private async Task DeleteVmAsync(CancellationToken cancellationToken)
    {
        if (JobId is null)
        {
            return;
        }

        // Plain (non-dotted) resw keys: see ShellViewModel.BuildStatusPillText's comment.
        var confirmed = await _dialogService.ConfirmAsync(
            _strings.GetString("ConfirmDeleteVm_Title"),
            _strings.GetString("ConfirmDeleteVm_Body"),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed)
        {
            return;
        }

        await RunVmActionAsync(() => _vmActions.DeleteVmAsync(JobId, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Not a <c>[RelayCommand]</c>: nothing outside this class ever needs
    /// to trigger a refresh manually today - it runs once whenever
    /// <see cref="JobId"/> changes (see <c>OnJobIdChanged</c>). A periodic
    /// re-read while a run is active is a real future improvement (the log
    /// otherwise only updates on navigation), tracked as a follow-up rather
    /// than added here as an unused command the wiring guard would then
    /// have to allowlist.
    /// </summary>
    private void RefreshLogTail()
    {
        if (JobId is null)
        {
            return;
        }

        LogTailText = string.Join(Environment.NewLine, _logTailReader.ReadLines(JobId));
    }

    private static string PhaseTitleKey(JobPhase phase) => phase switch
    {
        JobPhase.Draft or JobPhase.Validating => "PhaseValidating_Title",
        JobPhase.Uploading => "PhaseUploading_Title",
        JobPhase.Provisioning => "PhaseProvisioning_Title",
        JobPhase.Preparing => "PhasePreparing_Title",
        JobPhase.Running => "PhaseRunning_Title",
        JobPhase.Finalizing => "PhaseFinalizing_Title",
        JobPhase.Downloading => "PhaseDownloading_Title",
        JobPhase.Completed => "PhaseCompleted_Title",
        JobPhase.PartiallyCompleted => "PhasePartiallyCompleted_Title",
        JobPhase.Cancelling => "PhaseCancelling_Title",
        JobPhase.Cancelled => "PhaseCancelled_Title",
        JobPhase.Failed => "PhaseFailed_Title",
        _ => "PhaseValidating_Title",
    };

    private static bool IsTerminal(JobPhase phase) => phase is JobPhase.Completed or JobPhase.PartiallyCompleted or JobPhase.Cancelled or JobPhase.Failed;
}
