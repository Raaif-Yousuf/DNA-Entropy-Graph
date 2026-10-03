using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
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
    private readonly TimeProvider _time;
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
        TimeProvider time)
    {
        _runRepository = runRepository;
        _remover = remover;
        _navigator = navigator;
        _cloud = cloud;
        _local = local;
        _engine = engine;
        _dialogs = dialogs;
        _toasts = toasts;
        _strings = strings;
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
        var inputName = RunOptionsJson.TryDeserialize(run.OptionsJson)?.InputPath;
        return Contains(run.Name, text) || Contains(run.JobId, text) || Contains(run.Notes, text)
               || Contains(string.IsNullOrWhiteSpace(inputName) ? null : Path.GetFileName(inputName), text);

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
            new AsyncRelayCommand(() => RerunAsync(run), () => IsFinished(run.Phase)),
            new AsyncRelayCommand(() => RedownloadAsync(run)),
            new AsyncRelayCommand(() => DeleteCloudAsync(run)),
            new AsyncRelayCommand(() => DeleteLocalAsync(run)),
            new AsyncRelayCommand(() => RemoveAsync(run)));
    }

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
        return run.Phase == JobPhase.Failed || hasCode ? _strings.GetString(RunErrorCodes.ResourceKey(run.ErrorCode)) : null;
    }

    /// <summary>Why Delete cloud copy is off for this run (one reason, one action), or empty when it is on.</summary>
    private string? DeleteCloudHint(RunRecord run)
    {
        if (_cloud.CanDelete(run) && IsFinished(run.Phase))
        {
            // Null, not "": an empty tooltip string still pops up an empty box in WinUI.
            return null;
        }

        var key = !IsFinished(run.Phase) ? RunsCopy.DeleteCloudHintRunning
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
            _navigator.NavigateTo(ViewerViewModel.PageKey, run.OutputDir);
        }
        else
        {
            Toast(RunsCopy.OpenMissing);
        }
    }

    private Task RerunAsync(RunRecord run) => ActAsync(
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

            var input = await Task.Run(() => _local.FindRerunInput(run, options));
            if (input is null)
            {
                return RunsCopy.RerunNoInput;
            }

            var jobId = await _engine.StartRunAsync(options with { InputPath = input }, CancellationToken.None);
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

            return RunsCopy.Redownload(result.Status);
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

            await _remover.DeleteAsync(run.JobId, CancellationToken.None);
            return null;
        },
        RunsCopy.RemoveFailed);

    /// <summary>
    /// Runs one row action. Whatever it throws becomes the <paramref name="failure"/> toast (each names an action,
    /// Hard Rule 13), never an unobserved exception; the list is rebuilt afterwards so it shows what is really on disk.
    /// </summary>
    private async Task ActAsync(Func<Task<(string Title, string Body)?>> action, (string Title, string Body) failure)
    {
        (string Title, string Body)? copy;
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

    private void Toast((string Title, string Body) copy, params object[] args)
    {
        var body = _strings.GetString(copy.Body);
        _toasts.ShowToast(_strings.GetString(copy.Title), args.Length == 0 ? body : string.Format(CultureInfo.CurrentCulture, body, args));
    }
}
