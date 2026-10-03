using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
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

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private RunStatusFilterOption _selectedStatusFilter;

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
        var runs = await _runRepository.GetAllAsync(cancellationToken);
        _items = [.. runs.OrderByDescending(r => r.CreatedUtc).Select(BuildItem)];
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedStatusFilterChanged(RunStatusFilterOption value) => ApplyFilter();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var wanted = SelectedStatusFilter.Value;
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

    private RunListItem BuildItem(RunRecord run)
    {
        var local = TimeZoneInfo.ConvertTime(run.CreatedUtc, _time.LocalTimeZone);
        var (status, statusKey) = Classify(run.Phase);
        return new RunListItem(
            run,
            string.IsNullOrWhiteSpace(run.Name) ? run.JobId : run.Name,
            _strings.GetString(statusKey),
            status,
            local.ToString("t", CultureInfo.CurrentCulture),
            _local.OutputFolderExists(run),
            _cloud.IsAvailable(run),
            new RelayCommand(() => Open(run)),
            new AsyncRelayCommand(() => RerunAsync(run)),
            new AsyncRelayCommand(() => RedownloadAsync(run)),
            new AsyncRelayCommand(() => DeleteCloudAsync(run)),
            new AsyncRelayCommand(() => DeleteLocalAsync(run)),
            new AsyncRelayCommand(() => RemoveAsync(run)));
    }

    private static (RunStatusFilter Status, string Key) Classify(JobPhase phase) => phase switch
    {
        JobPhase.Completed => (RunStatusFilter.Completed, RunsCopy.StatusCompleted),
        JobPhase.PartiallyCompleted => (RunStatusFilter.Completed, RunsCopy.StatusPartial),
        JobPhase.Failed => (RunStatusFilter.Failed, RunsCopy.StatusFailed),
        JobPhase.Cancelled => (RunStatusFilter.Cancelled, RunsCopy.StatusCancelled),
        _ => (RunStatusFilter.Active, RunsCopy.StatusActive),
    };

    private void Open(RunRecord run)
    {
        if (!IsFinished(run.Phase))
        {
            _navigator.NavigateTo("RunProgress", run.JobId);
        }
        else if (_local.OutputFolderExists(run))
        {
            _navigator.NavigateTo(ViewerViewModel.PageKey, run.OutputDir);
        }
        else
        {
            Toast(RunsCopy.OpenMissing);
        }
    }

    private async Task RerunAsync(RunRecord run)
    {
        var options = RunOptionsJson.TryDeserialize(run.OptionsJson);
        if (options is null)
        {
            Toast(RunsCopy.RerunNoOptions);
            return;
        }

        var input = _local.FindRerunInput(run, options);
        if (input is null)
        {
            Toast(RunsCopy.RerunNoInput);
            return;
        }

        var jobId = await _engine.StartRunAsync(options with { InputPath = input }, CancellationToken.None);
        _navigator.NavigateTo("RunProgress", jobId);
    }

    private async Task RedownloadAsync(RunRecord run)
    {
        var status = await _cloud.RedownloadAsync(run, CancellationToken.None);
        Toast(RunsCopy.Redownload(status));
        await RefreshAsync(CancellationToken.None);
    }

    private async Task DeleteCloudAsync(RunRecord run)
    {
        if (!await _dialogs.ConfirmAsync(_strings.GetString(RunsCopy.DeleteCloudConfirmTitle), _strings.GetString(RunsCopy.DeleteCloudConfirmBody), CancellationToken.None))
        {
            return;
        }

        var status = await _cloud.DeleteAsync(run, CancellationToken.None);
        Toast(RunsCopy.DeleteCloud(status));
        await RefreshAsync(CancellationToken.None);
    }

    private async Task DeleteLocalAsync(RunRecord run)
    {
        var body = string.Format(CultureInfo.CurrentCulture, _strings.GetString(RunsCopy.DeleteLocalConfirmBody), run.OutputDir);
        if (!await _dialogs.ConfirmAsync(_strings.GetString(RunsCopy.DeleteLocalConfirmTitle), body, CancellationToken.None))
        {
            return;
        }

        Toast(RunsCopy.DeleteLocal(_local.DeleteOutputFolder(run)));
        await RefreshAsync(CancellationToken.None);
    }

    private async Task RemoveAsync(RunRecord run)
    {
        if (!IsFinished(run.Phase))
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(_strings.GetString(RunsCopy.RemoveConfirmTitle), _strings.GetString(RunsCopy.RemoveConfirmBody), CancellationToken.None))
        {
            return;
        }

        await _remover.DeleteAsync(run.JobId, CancellationToken.None);
        await RefreshAsync(CancellationToken.None);
    }

    private void Toast((string Title, string Body) copy) => _toasts.ShowToast(_strings.GetString(copy.Title), _strings.GetString(copy.Body));
}
