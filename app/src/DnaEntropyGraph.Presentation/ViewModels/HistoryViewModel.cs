using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Runs page (issue #101): every past run, grouped by day, filterable by status and text, with
/// open, re-run, re-download, delete cloud results, delete local files and remove.
/// Hard Rule 14: every deletion asks first and only ever reaches the run's own folder or job prefix;
/// removing a row touches neither. Hard Rule 13: every string is a Resources.resw key (see <see cref="RunsCopy"/>).
/// Sparklines on these rows are issue #118's.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IRunRepository _runRepository;
    private readonly IRunHistoryRemover _remover;
    private readonly INavigator _navigator;
    private readonly IRunCloudResults _cloud;
    private readonly ILocalRunFiles _local;
    private readonly IJobEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly IStringResourceProvider _strings;
    private readonly IShellLauncher _launcher;
    private readonly IGcpAccount _account;
    private readonly TimeProvider _time;
    private readonly IEntropyOverviewSource? _overview;
    private readonly IRunImporter? _importer;
    private readonly ImportedRunDismissals? _dismissals;
    private IReadOnlyList<RunListItem> _items = [];
    private int _loadGeneration;

    [ObservableProperty]
    private string _searchText = string.Empty;

    // Nullable because a TwoWay ComboBox binding writes null when its selection is cleared; null means "All".
    [ObservableProperty]
    private RunStatusFilterOption? _selectedStatusFilter;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    public HistoryViewModel(
        IRunRepository runRepository,
        IRunHistoryRemover remover,
        INavigator navigator,
        IRunCloudResults cloud,
        ILocalRunFiles local,
        IJobEngine engine,
        IDialogService dialogs,
        IToastService toasts,
        IStringResourceProvider strings,
        IShellLauncher launcher,
        IGcpAccount account,
        TimeProvider time,
        IEntropyOverviewSource? overview = null,
        IRunImporter? importer = null,
        ImportedRunDismissals? dismissals = null)
    {
        _dismissals = dismissals;
        _overview = overview;
        _importer = importer;
        _runRepository = runRepository;
        _remover = remover;
        _navigator = navigator;
        _cloud = cloud;
        _local = local;
        _engine = engine;
        _dialogs = dialogs;
        _toasts = toasts;
        _strings = strings;
        _launcher = launcher;
        _account = account;
        _time = time;
        StatusFilters =
        [
            new(RunStatusFilter.All, _strings.GetString("Runs_Filter_All")),
            new(RunStatusFilter.Active, _strings.GetString("Runs_Filter_Active")),
            new(RunStatusFilter.Completed, _strings.GetString("Runs_Filter_Completed")),
            new(RunStatusFilter.Failed, _strings.GetString("Runs_Filter_Failed")),
            new(RunStatusFilter.Cancelled, _strings.GetString("Runs_Filter_Cancelled")),
        ];
        _selectedStatusFilter = StatusFilters[0];
    }

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _importStatusText = string.Empty;

    public bool CanImport => _importer is not null;

    /// <summary>
    /// Issue #111: finds finished runs other computers left in this project's results bucket and adds them as read-only rows.
    /// The result is a status line on the page (what was found, or the one thing to do), never an escaped exception.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ImportRunsAsync(CancellationToken cancellationToken)
    {
        if (_importer is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_account.SelectedProjectId))
        {
            ImportStatusText = _strings.GetString(RunsCopy.ImportNoProject);
            return;
        }

        IsImporting = true;
        ImportStatusText = _strings.GetString(RunsCopy.ImportRunning);
        try
        {
            var result = await _importer.ImportAsync(_account.SelectedProjectId, cancellationToken);
            ImportStatusText = _strings.GetString(result.Imported switch { 0 => RunsCopy.ImportNone, 1 => RunsCopy.ImportDoneOne, _ => RunsCopy.ImportDoneMany });
            if (result.Imported > 0)
            {
                await RefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            ImportStatusText = string.Empty;
        }
        catch (Exception ex)
        {
            ImportStatusText = _strings.GetString(ImportFailureKey(ex));
        }
        finally
        {
            IsImporting = false;
        }
    }

    private static string ImportFailureKey(Exception failure) => failure switch
    {
        CloudOperationException { Kind: CloudErrorKind.Permission or CloudErrorKind.OrgPolicy } => RunsCopy.ImportFailedPermission,
        CloudOperationException { Kind: CloudErrorKind.Billing or CloudErrorKind.ApiDisabled } => RunsCopy.ImportFailedSetup,
        CloudOperationException { Kind: CloudErrorKind.Network } => RunsCopy.ImportFailedNetwork,
        AccountAuthException => RunsCopy.ImportFailedSignIn,
        _ => RunsCopy.ImportFailed,
    };

    public IReadOnlyList<RunStatusFilterOption> StatusFilters { get; }

    public ObservableCollection<RunDayGroup> Groups { get; } = [];

    /// <summary>True for a run that is over, whatever way it ended: the only runs that can be removed or fetched again.</summary>
    internal static bool IsFinished(JobPhase phase) => phase is JobPhase.Completed or JobPhase.PartiallyCompleted or JobPhase.Failed or JobPhase.Cancelled;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Every load (page open, Refresh, after an action) takes a number; only the newest may write the list, so a slow older
        // load that finishes last cannot put stale rows back.
        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            var runs = await _runRepository.GetAllAsync(cancellationToken);

            // One disk probe per row: off the UI thread.
            var rows = await Task.Run(() => runs.OrderByDescending(r => r.CreatedUtc).Select(r => (Run: r, HasFolder: _local.OutputFolderExists(r))).ToList(), cancellationToken);
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            _items = [.. rows.Select(r => BuildItem(r.Run, r.HasFolder))];
            ApplyFilter();
            SparklineWork = LoadSparklinesAsync(_items, generation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The page went away mid-load: nothing to show and nobody to tell.
        }
        catch (Exception)
        {
            // A load that was already replaced has nobody to tell: the list on screen is the newer one and is fine.
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            // The page calls this fire-and-forget on open, so an escaped exception would be unobserved: say so and name the action.
            Toast(RunsCopy.RefreshFailed);
        }
    }

    /// <summary>The sparkline reading started by the last load (issue #118); the list shows first and the lines fill in, so tests await this.</summary>
    public Task SparklineWork { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads each finished run's entropy track one at a time, off the UI thread, and puts a small line on its card. A run
    /// that was replaced by a newer load is skipped, and one run that cannot be read costs only its own line.
    /// </summary>
    private async Task LoadSparklinesAsync(IReadOnlyList<RunListItem> items, int generation, CancellationToken cancellationToken)
    {
        if (_overview is null)
        {
            return;
        }

        foreach (var item in items)
        {
            var folder = item.Run.OutputDir;
            if (!item.HasLocalFiles || string.IsNullOrWhiteSpace(folder) || item.Run.Phase is not (JobPhase.Completed or JobPhase.PartiallyCompleted))
            {
                continue;
            }

            if (generation != Volatile.Read(ref _loadGeneration) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var contig = (await Task.Run(() => _overview.Load(folder), CancellationToken.None))?.Contigs.FirstOrDefault();
                if (contig is not null)
                {
                    item.Sparkline = TrackResampler.Resample(contig.Means, RunListItem.SparklinePoints);
                }
            }
            catch (Exception)
            {
                // No line on this card; the rest of the list is unaffected.
            }
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedStatusFilterChanged(RunStatusFilterOption? value) => ApplyFilter();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var wanted = SelectedStatusFilter?.Value ?? RunStatusFilter.All;
        var visible = _items
            .Where(i => wanted == RunStatusFilter.All || i.Status == wanted)
            .Where(i => text.Length == 0 || Matches(i.Run, text))
            .ToList();

        var today = DayOf(_time.GetUtcNow());
        Groups.Clear();
        foreach (var day in visible.GroupBy(i => DayOf(i.Run.CreatedUtc)).OrderByDescending(g => g.Key))
        {
            Groups.Add(new RunDayGroup(HeaderFor(day.Key, today), day.ToList()));
        }

        IsEmpty = visible.Count == 0;
        EmptyText = !IsEmpty ? string.Empty : _strings.GetString(_items.Count == 0 ? RunsCopy.EmptyNone : RunsCopy.EmptyNoMatch);
    }

    private static bool Matches(RunRecord run, string text)
    {
        var options = RunOptionsJson.TryDeserialize(run.OptionsJson);
        var inputName = options?.InputPath;
        return Contains(run.Name, text) || Contains(run.JobId, text) || Contains(run.Notes, text)
               || Contains(string.IsNullOrWhiteSpace(inputName) ? null : Path.GetFileName(inputName), text)
               || Contains(options?.Accession, text) || Contains(options?.AccessionRange, text);

        static bool Contains(string? haystack, string needle) => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
    }

    private DateOnly DayOf(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).DateTime);

    private string HeaderFor(DateOnly day, DateOnly today)
        => day == today ? _strings.GetString(RunsCopy.GroupToday)
            : day == today.AddDays(-1) ? _strings.GetString(RunsCopy.GroupYesterday)
            : day.ToString("D", CultureInfo.CurrentCulture);

    private RunListItem BuildItem(RunRecord run, bool hasFolder)
    {
        var local = TimeZoneInfo.ConvertTime(run.CreatedUtc, _time.LocalTimeZone);
        var (status, statusKey) = Classify(run.Phase);
        return new RunListItem(
            run,
            string.IsNullOrWhiteSpace(run.Name) ? run.JobId : run.Name,
            _strings.GetString(statusKey),
            status,
            local.ToString("t", CultureInfo.CurrentCulture),
            hasFolder,
            _cloud.IsAvailable(run),
            _cloud.CanDelete(run),
            DeleteCloudHint(run),
            ReasonFor(run),
            new AsyncRelayCommand(() => OpenAsync(run)),
            new AsyncRelayCommand(() => RerunAsync(run), () => IsFinished(run.Phase) && !run.Imported),
            new AsyncRelayCommand(() => RedownloadAsync(run)),
            new AsyncRelayCommand(() => DeleteCloudAsync(run)),
            new AsyncRelayCommand(() => DeleteLocalAsync(run)),
            new AsyncRelayCommand(() => RemoveAsync(run)),
            OwnerRequestFor(run),
            WaitForGpuFor(run))
        {
            PresetText = PresetLine(run),
            DeleteLocalHint = run.IsPrototypeImport ? _strings.GetString(RunsCopy.DeleteLocalHintImported) : null,
            IsImported = run.IsCloudImport,
            ImportedText = run.IsCloudImport ? string.Format(CultureInfo.CurrentCulture, _strings.GetString(RunsCopy.ImportedBadge), ShortInstallation(run.InstallationId)) : null,
            RedownloadLabel = _strings.GetString(run.Phase == JobPhase.Cancelled ? RunsCopy.RedownloadPartialLabel : RunsCopy.RedownloadAgainLabel),
        };
    }

    private static string ShortInstallation(string? id) => string.IsNullOrWhiteSpace(id) ? "?" : id.Length > 8 ? id[..8] : id;

    /// <summary>The "Preset: name" line of a run started from an option preset (issue #136), read from the options the run recorded.</summary>
    private string? PresetLine(RunRecord run)
        => RunOptionsJson.TryDeserialize(run.OptionsJson)?.PresetName is { Length: > 0 } name
            ? string.Format(CultureInfo.CurrentCulture, _strings.GetString(RunsCopy.PresetLine), name)
            : null;

    /// <summary>
    /// The reason line for a finished run (issue #459). A Failed run always has one (an unknown or missing code gets the
    /// generic message); any other finished run has one only when it was recorded with a code, such as a Completed run
    /// whose VM end could not be confirmed. A run still going has none.
    /// </summary>
    private string? ReasonFor(RunRecord run)
    {
        if (!IsFinished(run.Phase))
        {
            return null;
        }

        var hasCode = !string.IsNullOrWhiteSpace(run.ErrorCode);
        // Issue #92: a stockout after the run already waited cannot say "press Wait"; it says the wait ran out.
        if (run.Phase == JobPhase.Failed && IsStockout(run) && RunOptionsJson.TryDeserialize(run.OptionsJson)?.WaitForGpu == true)
        {
            return _strings.GetString("RunError_stockout_waited");
        }

        return run.Phase == JobPhase.Failed || hasCode ? _strings.GetString(RunErrorCodes.ResourceKey(run.ErrorCode)) : null;
    }

    /// <summary>
    /// Issue #604: the "Copy request for the project owner" action of a run that failed because the user's account may not
    /// run a VM as the worker account. Null for every other run, and for a run whose project is unknown (the account's
    /// email is built from it).
    /// </summary>
    private RelayCommand? OwnerRequestFor(RunRecord run)
        => RunErrorCodes.IsActAsRefusal(run.ErrorCode, run.ErrorDetail) && !string.IsNullOrWhiteSpace(run.ProjectId)
            ? new RelayCommand(() => CopyOwnerRequest(run.ProjectId, WorkerAccountDetail.TryRead(run.ErrorDetail)))
            : null;

    /// <summary>
    /// Copies the owner request for the account the refused VM was to run as (<paramref name="vmAccount"/>, read from the row;
    /// the app's worker account when the row does not say), naming the signed-in user as the grantee when there is one. The
    /// user's email goes to the clipboard only, never to a log.
    /// </summary>
    private void CopyOwnerRequest(string projectId, string? vmAccount)
    {
        var email = _account.CurrentAccount?.Email;
        var grantee = string.IsNullOrWhiteSpace(email) ? string.Empty : string.Format(CultureInfo.CurrentCulture, _strings.GetString(RunsCopy.OwnerRequestGrantee), email);
        var template = _strings.GetString(RunsCopy.OwnerRequestActAs);
        var text = string.Format(CultureInfo.CurrentCulture, template, WorkerIdentityNames.ActAsRole, vmAccount ?? WorkerIdentityNames.ServiceAccountEmail(projectId), projectId, grantee);
        Toast(_launcher.CopyText(text) ? RunsCopy.OwnerRequestCopied : RunsCopy.OwnerRequestCopyFailed);
    }

    /// <summary>Why Delete cloud copy is off for this run (one reason, one action), or empty when it is on.</summary>
    private string? DeleteCloudHint(RunRecord run)
    {
        if (_cloud.CanDelete(run) && IsFinished(run.Phase))
        {
            // Null, not "": an empty tooltip string still pops up an empty box in WinUI.
            return null;
        }

        var key = run.IsCloudImport ? RunsCopy.DeleteCloudHintImported
            : run.IsPrototypeImport ? RunsCopy.DeleteCloudHintPrototype
            : !IsFinished(run.Phase) ? RunsCopy.DeleteCloudHintRunning
            : !_cloud.IsAvailable(run) ? RunsCopy.DeleteCloudHintNoCopy
            : RunsCopy.DeleteCloudHintNotConnected;
        return _strings.GetString(key);
    }

    private static (RunStatusFilter Status, string Key) Classify(JobPhase phase) => phase switch
    {
        JobPhase.Completed => (RunStatusFilter.Completed, RunsCopy.StatusCompleted),
        JobPhase.PartiallyCompleted => (RunStatusFilter.Completed, RunsCopy.StatusPartial),
        JobPhase.Failed => (RunStatusFilter.Failed, RunsCopy.StatusFailed),
        JobPhase.Cancelled => (RunStatusFilter.Cancelled, RunsCopy.StatusCancelled),
        _ => (RunStatusFilter.Active, RunsCopy.StatusActive),
    };

    private async Task OpenAsync(RunRecord run)
    {
        if (!IsFinished(run.Phase))
        {
            _navigator.NavigateTo("RunProgress", run.JobId);
            return;
        }

        // The folder may have gone since the list was built (and a network drive can be slow): probe off the UI thread.
        if (await Task.Run(() => _local.OutputFolderExists(run)))
        {
            _navigator.NavigateTo(ResultsViewModel.PageKey, run.JobId);
        }
        else
        {
            Toast(RunsCopy.OpenMissing);
        }
    }

    private static bool IsStockout(RunRecord run) => run.ErrorCode == RunErrorCodes.For(CloudErrorKind.Stockout);

    /// <summary>
    /// Issue #92: the "Wait for a graphics card" action of a run that failed with a stockout and did not already wait. Null for every other run, and for one whose
    /// options cannot be read (the ordinary Run again reports that).
    /// </summary>
    private AsyncRelayCommand? WaitForGpuFor(RunRecord run)
        => run.Phase == JobPhase.Failed && IsStockout(run) && RunOptionsJson.TryDeserialize(run.OptionsJson) is { WaitForGpu: false }
            ? new AsyncRelayCommand(() => RerunAsync(run, waitForGpu: true))
            : null;

    private Task RerunAsync(RunRecord run, bool waitForGpu = false) => ActAsync(
        async () =>
        {
            // A run still going owns its VM; starting another from its row would rent a second one.
            if (!IsFinished(run.Phase))
            {
                return null;
            }

            var options = RunOptionsJson.TryDeserialize(run.OptionsJson);
            if (options is null)
            {
                return RunsCopy.RerunNoOptions;
            }

            // Issue #684: an accession run has no file; it starts again from the same accession.
            var input = options.Accession is not null ? null : await Task.Run(() => _local.FindRerunInput(run, options));
            if (input is null && options.Accession is null)
            {
                return RunsCopy.RerunNoInput;
            }

            // Issue #686: a paired run starts again with its GFF3. One that cannot be found is a missing input, never a silent run without it.
            string? annotation = null;
            if (!string.IsNullOrWhiteSpace(options.AnnotationPath))
            {
                annotation = await Task.Run(() => _local.FindRerunAnnotation(run, options));
                if (annotation is null)
                {
                    return RunsCopy.RerunNoInput;
                }
            }

            var jobId = await _engine.StartRunAsync(options with { InputPath = input, AnnotationPath = annotation, WaitForGpu = waitForGpu || options.WaitForGpu }, CancellationToken.None);
            _navigator.NavigateTo("RunProgress", jobId);
            return null;
        },
        RunsCopy.RerunFailed);

    private Task RedownloadAsync(RunRecord run) => ActAsync(
        async () =>
        {
            var result = await _cloud.RedownloadAsync(run, CancellationToken.None);
            if (result.ChangedKeptAside > 0)
            {
                // Files the user may have edited were renamed, not overwritten: say how many and where to look.
                Toast(RunsCopy.RedownloadChangedKeptAside(result.ChangedKeptAside), result.ChangedKeptAside);
            }

            // A cancelled run's partial outputs are the expected result of its cancel, not a warning that something is missing.
            return run.Phase == JobPhase.Cancelled && result.Status == CloudResultsStatus.Partial ? RunsCopy.RedownloadCancelledPartial : RunsCopy.Redownload(result.Status);
        },
        RunsCopy.Redownload(CloudResultsStatus.Failed));

    private Task DeleteCloudAsync(RunRecord run) => ActAsync(
        async () =>
        {
            if (!await _dialogs.ConfirmAsync(_strings.GetString(RunsCopy.DeleteCloudConfirmTitle), _strings.GetString(RunsCopy.DeleteCloudConfirmBody), CancellationToken.None))
            {
                return null;
            }

            return RunsCopy.DeleteCloud(await _cloud.DeleteAsync(run, CancellationToken.None));
        },
        RunsCopy.DeleteCloud(CloudResultsStatus.Failed));

    private Task DeleteLocalAsync(RunRecord run) => ActAsync(
        async () =>
        {
            // A run that is still going owns its output folder; deleting under it would corrupt the run.
            if (!IsFinished(run.Phase))
            {
                return null;
            }

            // Hard Rule 14: an imported run's folder is the user's own. The button is off; this stops a command fired another way.
            if (run.Imported)
            {
                return RunsCopy.DeleteLocal(LocalDeleteStatus.Refused);
            }

            var body = string.Format(CultureInfo.CurrentCulture, _strings.GetString(RunsCopy.DeleteLocalConfirmBody), run.OutputDir);
            if (!await _dialogs.ConfirmAsync(_strings.GetString(RunsCopy.DeleteLocalConfirmTitle), body, CancellationToken.None))
            {
                return null;
            }

            // Recursive disk work: off the UI thread.
            var result = await Task.Run(() => _local.DeleteOutputFolder(run));
            if (result.Status != LocalDeleteStatus.Partial)
            {
                return RunsCopy.DeleteLocal(result.Status);
            }

            // Partial says what went and what stayed, so it carries the counts the other outcomes do not.
            Toast(RunsCopy.DeleteLocal(LocalDeleteStatus.Partial), result.FilesDeleted, result.FilesRemaining);
            return null;
        },
        RunsCopy.DeleteLocal(LocalDeleteStatus.InUse));

    private Task RemoveAsync(RunRecord run) => ActAsync(
        async () =>
        {
            if (!IsFinished(run.Phase)
                || !await _dialogs.ConfirmAsync(_strings.GetString(RunsCopy.RemoveConfirmTitle), _strings.GetString(RunsCopy.RemoveConfirmBody), CancellationToken.None))
            {
                return null;
            }

            // Issue #111: remember an imported run was dismissed, or the next scan would add it straight back.
            // Only a bucket-found run: a prototype run's id can never come back from the bucket scan, so it does not belong in that setting.
            if (run.IsCloudImport)
            {
                _dismissals?.Add(run.JobId);
            }

            await _remover.DeleteAsync(run.JobId, CancellationToken.None);
            return null;
        },
        RunsCopy.RemoveFailed);

    /// <summary>
    /// Runs one row action. Whatever it throws becomes the <paramref name="failure"/> toast (each names an action,
    /// Hard Rule 13), never an unobserved exception; the list is rebuilt afterwards so it shows what is really on disk.
    /// </summary>
    private async Task ActAsync(Func<Task<RunsMessage?>> action, RunsMessage failure)
    {
        RunsMessage? copy;
        try
        {
            copy = await action();
        }
        catch (Exception)
        {
            copy = failure;
        }

        if (copy is { } shown)
        {
            Toast(shown);
        }

        await RefreshAsync(CancellationToken.None);
    }

    private void Toast(RunsMessage copy, params object[] args)
    {
        var body = _strings.GetString(copy.Body);
        _toasts.ShowToast(_strings.GetString(copy.Title), args.Length == 0 ? body : string.Format(CultureInfo.CurrentCulture, body, args), copy.Severity);
    }
}
