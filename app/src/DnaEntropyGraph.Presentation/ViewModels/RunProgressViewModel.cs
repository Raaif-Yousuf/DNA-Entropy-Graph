using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
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
        IMessenger messenger)
    {
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

        await _jobEngine.CancelRunAsync(JobId, cancellationToken).ConfigureAwait(false);
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

        await _vmActions.StopVmAsync(JobId, cancellationToken).ConfigureAwait(false);
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

        await _vmActions.DeleteVmAsync(JobId, cancellationToken).ConfigureAwait(false);
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
