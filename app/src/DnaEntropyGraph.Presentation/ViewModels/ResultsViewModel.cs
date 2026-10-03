using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Results page for a finished run (issue #102): a per-contig stats header and the run's output files.
/// Everything shown is read from the run's own output folder (<see cref="RunRecord.OutputDir"/>): the numbers from the
/// worker's summary file, never recomputed here, and the file list from the folder itself. Read-only (Hard Rule 14).
/// Seams for later issues: <see cref="Files"/> and the run folder are what an "Open in IGV or Geneious" action (#586)
/// and an "Export PNG" action (#587) would act on, and gene and region tables (#552) would add groups beside <see cref="Groups"/>.
/// None of those are built here.
/// </summary>
public sealed partial class ResultsViewModel : ObservableObject
{
    /// <summary>The <c>INavigator.NavigateTo</c> key of the Results page; the parameter is the run's job id (string).</summary>
    public const string PageKey = "Results";

    private const double Kilobyte = 1024;
    private const double Megabyte = 1024 * 1024;

    private readonly IRunRepository _runRepository;
    private readonly IRunOutputReader _outputReader;
    private readonly IShellLauncher _launcher;
    private readonly INavigator _navigator;
    private readonly IStringResourceProvider _strings;
    private string? _folder;
    private int _loadGeneration;

    [ObservableProperty]
    private string _title = string.Empty;

    // Why the whole page has nothing to show (no such run, folder gone, unreadable): one reason, one action.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoticeVisible))]
    private string _noticeText = string.Empty;

    // Why the numbers are missing while the files are fine.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatsNoticeVisible))]
    private string _statsNoticeText = string.Empty;

    // Why the last Open, Show in folder, Copy path or Open folder did nothing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActionNoticeVisible))]
    private string _actionNoticeText = string.Empty;

    public ResultsViewModel(
        IRunRepository runRepository,
        IRunOutputReader outputReader,
        IShellLauncher launcher,
        INavigator navigator,
        IStringResourceProvider strings)
    {
        _runRepository = runRepository;
        _outputReader = outputReader;
        _launcher = launcher;
        _navigator = navigator;
        _strings = strings;
    }

    public ObservableCollection<ResultsStatsGroup> Groups { get; } = [];

    public ObservableCollection<ResultsFileItem> Files { get; } = [];

    public bool IsNoticeVisible => NoticeText.Length > 0;

    public bool IsStatsNoticeVisible => StatsNoticeText.Length > 0;

    public bool IsActionNoticeVisible => ActionNoticeText.Length > 0;

    public bool HasFiles => Files.Count > 0;

    /// <summary>
    /// Loads the run with this id. The page calls it on every navigation and does not await it, so it never throws:
    /// a failure becomes the notice on the page. A second run replaces the first.
    /// </summary>
    public async Task LoadAsync(string? jobId, CancellationToken cancellationToken = default)
    {
        // Only the newest load may write the page, so a slow older one finishing last cannot put its run back.
        var generation = Interlocked.Increment(ref _loadGeneration);
        Reset();
        try
        {
            var runs = await _runRepository.GetAllAsync(cancellationToken);
            var run = runs.FirstOrDefault(r => r.JobId == jobId);
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            if (run is null)
            {
                NoticeText = _strings.GetString(ResultsCopy.NoRun);
                return;
            }

            Title = string.IsNullOrWhiteSpace(run.Name) ? run.JobId : run.Name;
            var folder = run.OutputDir;

            // Listing a folder and reading its summaries is disk work (maybe a network drive): off the UI thread.
            var snapshot = string.IsNullOrWhiteSpace(folder) ? null : await Task.Run(() => _outputReader.Read(folder), cancellationToken);
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            if (snapshot is null)
            {
                NoticeText = _strings.GetString(ResultsCopy.FolderMissing);
                return;
            }

            Show(folder!, snapshot);
        }
        catch (OperationCanceledException)
        {
            // The page went away mid-load: nothing to show and nobody to tell.
        }
        catch (Exception)
        {
            // Not rethrown: the page does not await this, so an escaped exception would be unobserved and the page left blank.
            if (generation == Volatile.Read(ref _loadGeneration))
            {
                NoticeText = _strings.GetString(ResultsCopy.ReadFailed);
            }
        }
    }

    private void Reset()
    {
        _folder = null;
        Title = string.Empty;
        NoticeText = string.Empty;
        StatsNoticeText = string.Empty;
        ActionNoticeText = string.Empty;
        Groups.Clear();
        Files.Clear();
        OnPropertyChanged(nameof(HasFiles));
        OpenFolderCommand.NotifyCanExecuteChanged();
        OpenInViewerCommand.NotifyCanExecuteChanged();
    }

    private void Show(string folder, RunOutputSnapshot snapshot)
    {
        _folder = folder;
        foreach (var file in snapshot.Files)
        {
            Files.Add(new ResultsFileItem(
                file.RelativePath,
                SizeText(file.Bytes),
                new RelayCommand(() => Report(_launcher.OpenFile(file.FullPath), ResultsCopy.FileActionFailed)),
                new RelayCommand(() => Report(_launcher.ShowInFolder(file.FullPath), ResultsCopy.FileActionFailed)),
                new RelayCommand(() => Report(_launcher.CopyText(file.FullPath), ResultsCopy.CopyFailed))));
        }

        var several = snapshot.Summaries.Count > 1;
        foreach (var summary in snapshot.Summaries.Where(s => s.Summary is not null))
        {
            Groups.Add(BuildGroup(several ? summary.RelativePath : string.Empty, summary.Summary!));
        }

        var unreadable = snapshot.Summaries.FirstOrDefault(s => s.Summary is null);
        StatsNoticeText = snapshot.Summaries.Count == 0
            ? _strings.GetString(ResultsCopy.StatsNone)
            : unreadable is null ? string.Empty : Format(ResultsCopy.StatsUnreadable, unreadable.RelativePath);

        OnPropertyChanged(nameof(HasFiles));
        OpenFolderCommand.NotifyCanExecuteChanged();
        OpenInViewerCommand.NotifyCanExecuteChanged();
    }

    private ResultsStatsGroup BuildGroup(string title, RunSummary summary)
        => new(
            title,
            Format(ResultsCopy.Headline, summary.MeanAll, summary.MinAll, summary.MaxAll, summary.TotalLength, summary.Records),
            [.. summary.Contigs.Select(c => new ResultsContigRow(
                c.Name,
                Format(ResultsCopy.Length, c.Length),
                Format(ResultsCopy.Bits, c.Mean),
                Format(ResultsCopy.Bits, c.Min),
                Format(ResultsCopy.Bits, c.Max),
                c.Direction ?? string.Empty))]);

    private string SizeText(long bytes) => bytes switch
    {
        >= (long)Megabyte => Format(ResultsCopy.SizeMegabytes, bytes / Megabyte),
        >= (long)Kilobyte => Format(ResultsCopy.SizeKilobytes, bytes / Kilobyte),
        _ => Format(ResultsCopy.SizeBytes, bytes),
    };

    private string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);

    // A success clears the last failure, so the page never keeps naming a problem that has since been worked around.
    private void Report(bool succeeded, string failureKey) => ActionNoticeText = succeeded ? string.Empty : _strings.GetString(failureKey);

    private bool HasFolder() => _folder is not null;

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void OpenFolder() => Report(_folder is not null && _launcher.OpenFolder(_folder), ResultsCopy.OpenFolderFailed);

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void OpenInViewer() => _navigator.NavigateTo(ViewerViewModel.PageKey, _folder);

    [RelayCommand]
    private void ShowRuns() => _navigator.NavigateTo("Runs");
}
