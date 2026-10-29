using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;

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
    private readonly IExternalViewerOpener _viewers;
    private readonly IEntropyOverviewSource? _overviewSource;
    private readonly IOverviewImageExporter? _imageExporter;
    private readonly IGeneTableSource? _geneTables;
    private IReadOnlyList<RunOutputFile> _outputFiles = [];
    private string? _folder;
    private RunRecord? _run;
    private readonly ResultsExportRoot? _exportRoot;
    private int _loadGeneration;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>The run's cost line (issues #100, #557): the booked estimate, or no cloud cost for a run on this PC; empty when the run booked nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCost))]
    private string _costText = string.Empty;

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

    private readonly Func<string?, bool>? _hasSpotPrice;

    private readonly IRunCostSource? _costs;

    private readonly IDiagnosticsLog? _log;

    public ResultsViewModel(
        IRunRepository runRepository,
        IRunOutputReader outputReader,
        IShellLauncher launcher,
        INavigator navigator,
        IStringResourceProvider strings,
        IExternalViewerOpener viewers,
        IEntropyOverviewSource? overviewSource = null,
        IOverviewImageExporter? imageExporter = null,
        IGeneTableSource? geneTables = null,
        ResultsExportRoot? exportRoot = null,
        IPricingSource? pricing = null,
        IRunCostSource? costs = null,
        IDiagnosticsLog? log = null)
    {
        _geneTables = geneTables;
        _exportRoot = exportRoot;
        _costs = costs;
        _log = log;
        _hasSpotPrice = RunCostText.SpotPriceLookup(pricing);
        _overviewSource = overviewSource;
        _imageExporter = imageExporter;
        _runRepository = runRepository;
        _outputReader = outputReader;
        _launcher = launcher;
        _navigator = navigator;
        _strings = strings;
        _viewers = viewers;
    }

    /// <summary>The entropy overview strip (issue #118): the run's first contig, binned; null when the run has no entropy track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverview))]
    [NotifyPropertyChangedFor(nameof(OverviewTitle))]
    [NotifyCanExecuteChangedFor(nameof(ExportOverviewPngCommand))]
    private EntropyContig? _overview;

    public bool HasOverview => Overview is not null;

    public string OverviewTitle => Overview?.Name ?? string.Empty;

    public ObservableCollection<ResultsStatsGroup> Groups { get; } = [];

    /// <summary>Issue #552: one sortable table per <c>*.genes.tsv</c> in the run folder (only the readable ones).</summary>
    public ObservableCollection<ResultsGeneTable> GeneTables { get; } = [];

    public bool HasGeneTables => GeneTables.Count > 0;

    // Why a gene table is missing while the rest of the page is fine: one reason, one action.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneNoticeVisible))]
    private string _geneNoticeText = string.Empty;

    public bool IsGeneNoticeVisible => GeneNoticeText.Length > 0;

    public ObservableCollection<ResultsFileItem> Files { get; } = [];

    public bool HasCost => CostText.Length > 0;

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
        CancelViewerOpen();
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
            _run = run;
            var booked = await RunCostText.ReadBookedAsync(_costs, cancellationToken, _log);
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            CostText = RunCostText.Header(run, booked.TryGetValue(run.JobId, out var usd) ? usd : null, _strings, _hasSpotPrice) ?? string.Empty;
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

            await ShowGeneTablesAsync(folder!, generation);

            // The strip fills in after the page: reading a big track is disk work and must not hold the stats back.
            if (_overviewSource is not null)
            {
                var overview = await Task.Run(() => _overviewSource.Load(folder!), CancellationToken.None);
                if (generation == Volatile.Read(ref _loadGeneration))
                {
                    Overview = overview?.Contigs.FirstOrDefault();
                }
            }
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
        _run = null;
        _outputFiles = [];
        Title = string.Empty;
        CostText = string.Empty;
        Overview = null;
        NoticeText = string.Empty;
        StatsNoticeText = string.Empty;
        ActionNoticeText = string.Empty;
        Groups.Clear();
        GeneTables.Clear();
        GeneNoticeText = string.Empty;
        OnPropertyChanged(nameof(HasGeneTables));
        Files.Clear();
        OnPropertyChanged(nameof(HasFiles));
        OpenFolderCommand.NotifyCanExecuteChanged();
        OpenInViewerCommand.NotifyCanExecuteChanged();
        OpenInIgvCommand.NotifyCanExecuteChanged();
        OpenInGeneiousCommand.NotifyCanExecuteChanged();
    }

    // The tables are disk work (a big genome has thousands of rows): off the UI thread, and only the newest load may show them.
    private async Task ShowGeneTablesAsync(string folder, int generation)
    {
        if (_geneTables is null)
        {
            return;
        }

        var tables = await Task.Run(() => _geneTables.Load(folder), CancellationToken.None);
        if (generation != Volatile.Read(ref _loadGeneration))
        {
            return;
        }

        var several = tables.Count > 1;
        var notices = new List<string>();
        foreach (var table in tables.Where(t => t.Content is not null))
        {
            GeneTables.Add(new ResultsGeneTable(several ? table.RelativePath : string.Empty, table.Content!.Rows, _strings));
            if (table.Content.SkippedRows > 0)
            {
                notices.Add(Format(ResultsCopy.GenesRowsSkipped, table.Content.SkippedRows, table.RelativePath));
            }
        }

        // Every unreadable table is reported, with how many there are, so none goes missing without a word.
        var unreadable = tables.Where(t => t.Content is null).Select(t => t.RelativePath).ToList();
        if (unreadable.Count > 0)
        {
            notices.Add(Format(ResultsCopy.GenesUnreadable, unreadable.Count, string.Join(", ", unreadable)));
        }

        GeneNoticeText = string.Join(" ", notices);
        OnPropertyChanged(nameof(HasGeneTables));
    }

    private void Show(string folder, RunOutputSnapshot snapshot)
    {
        _folder = folder;
        _outputFiles = snapshot.Files;
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
        OpenInIgvCommand.NotifyCanExecuteChanged();
        OpenInGeneiousCommand.NotifyCanExecuteChanged();
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
                DirectionText(c.Direction)))]);

    private string DirectionText(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        var key = ResultsCopy.DirectionKey(token);
        return key == ResultsCopy.DirectionUnknown ? Format(key, token) : _strings.GetString(key);
    }

    private string SizeText(long? bytes) => bytes switch
    {
        null => _strings.GetString(ResultsCopy.SizeUnknown),
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

    /// <summary>Issue #699: a range dragged on the overview strip opens the embedded viewer at that range. A click opens nothing.</summary>
    [RelayCommand]
    private void SelectOverviewRange(OverviewRange range)
    {
        if (Overview is { } contig && _folder is { } folder && OverviewSelection.ToTarget(folder, contig, range) is { } target)
        {
            _navigator.NavigateTo(ViewerViewModel.PageKey, target);
        }
    }

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void OpenInViewer() => _navigator.NavigateTo(ViewerViewModel.PageKey, _folder);

    // Issue #586. The viewer opener never throws for an expected failure; anything else is shown as "would not start" rather
    // than escaping into a task nobody observes. A success clears the last failure.
    [RelayCommand(CanExecute = nameof(HasFolder))]
    private Task OpenInIgvAsync(CancellationToken cancellationToken)
        => OpenViewerAsync(ct => _viewers.OpenInIgvAsync(_outputFiles, ct), ViewerKeys.Igv, cancellationToken);

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private Task OpenInGeneiousAsync(CancellationToken cancellationToken)
        => OpenViewerAsync(ct => _viewers.OpenInGeneiousAsync(_outputFiles, ct), ViewerKeys.Geneious, cancellationToken);

    /// <summary>
    /// Stops an Open in IGV or Open in Geneious that is still waiting (IGV can take up to a minute to answer). The page calls it
    /// when it is left; a run loaded in its place does the same, so a late answer never writes onto another run's page.
    /// </summary>
    public void CancelViewerOpen()
    {
        OpenInIgvCommand.Cancel();
        OpenInGeneiousCommand.Cancel();
    }

    private async Task OpenViewerAsync(Func<CancellationToken, Task<ExternalViewerOutcome>> open, ViewerKeys keys, CancellationToken cancellationToken)
    {
        ExternalViewerOutcome outcome;
        try
        {
            outcome = await open(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            outcome = ExternalViewerOutcome.LaunchFailed;
        }

        var key = outcome switch
        {
            ExternalViewerOutcome.Opened => null,
            ExternalViewerOutcome.NoFiles => keys.NoFiles,
            ExternalViewerOutcome.NotFound => keys.NotFound,
            ExternalViewerOutcome.IgvRejected => ResultsCopy.IgvRejected,
            ExternalViewerOutcome.IgvNoReply => ResultsCopy.IgvNoReply,
            ExternalViewerOutcome.IgvNoAnswer => ResultsCopy.IgvNoAnswer,
            ExternalViewerOutcome.UnsafeProgramArguments => ResultsCopy.ViewerUnsafePath,
            ExternalViewerOutcome.IgvPathUnsendable => ResultsCopy.IgvPathUnsendable,
            ExternalViewerOutcome.NoGenome => ResultsCopy.IgvNoGenome,
            ExternalViewerOutcome.OpenedFirstInputOnly => ResultsCopy.IgvFirstInputOnly,
            _ => keys.LaunchFailed,
        };
        ActionNoticeText = key is null ? string.Empty : _strings.GetString(key);
    }

    private sealed record ViewerKeys(string NoFiles, string NotFound, string LaunchFailed)
    {
        public static readonly ViewerKeys Igv = new(ResultsCopy.IgvNoFiles, ResultsCopy.IgvNotFound, ResultsCopy.IgvLaunchFailed);
        public static readonly ViewerKeys Geneious = new(ResultsCopy.GeneiousNoFiles, ResultsCopy.GeneiousNotFound, ResultsCopy.GeneiousLaunchFailed);
    }

    private bool CanExportOverviewPng() => Overview is not null && _folder is not null && _imageExporter is not null;

    /// <summary>
    /// Saves the overview as a PNG in the run's own folder (never next to the input, Hard Rule 14) and shows it in File Explorer. A run imported
    /// from the earlier version has a folder that is the user's own: its PNG goes to a new folder under the default output folder instead.
    /// A file that already exists is never overwritten; the new one gets a number.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportOverviewPng))]
    private async Task ExportOverviewPngAsync()
    {
        if (Overview is not { } contig || _folder is not { } folder || _imageExporter is not { } exporter)
        {
            return;
        }

        var run = _run;
        string? path = null;
        var written = false;
        if (run is { Imported: true } && _exportRoot is null)
        {
            // No safe place to write: say so rather than fall back to the user's own folder.
            ActionNoticeText = _strings.GetString(ResultsCopy.ExportPngFailed);
            return;
        }

        try
        {
            (path, written) = await Task.Run(
                () =>
                {
                    var target = run is { Imported: true } && _exportRoot is not null
                        ? RunOutputFolders.CreateUnique(_exportRoot.Parent(), (string.IsNullOrWhiteSpace(run.Name) ? run.JobId : run.Name) + " exports")
                        : folder;
                    var file = RunOutputFolders.UniqueFilePath(target, SafeFileName(contig.Name) + ".entropy.overview", ".png");
                    return (file, exporter.Export(contig, file));
                },
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            written = false;
        }

        if (written && path is not null)
        {
            Report(_launcher.ShowInFolder(path), ResultsCopy.FileActionFailed);
        }
        else
        {
            ActionNoticeText = _strings.GetString(ResultsCopy.ExportPngFailed);
        }
    }

    // A contig name comes from the user's own file: only its letters, digits and a few safe marks may reach a file name.
    private static string SafeFileName(string contigName)
    {
        var safe = new string([.. contigName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_')]).Trim('.');
        return safe.Length == 0 ? "entropy" : safe.Replace("..", "_");
    }

    [RelayCommand]
    private void ShowRuns() => _navigator.NavigateTo("Runs");
}


