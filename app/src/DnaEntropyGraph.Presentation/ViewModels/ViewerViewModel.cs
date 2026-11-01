using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Palettes;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Core.Viewers;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;

namespace DnaEntropyGraph.Presentation.ViewModels;

public enum ViewerStatus
{
    Idle,
    NoRun,
    RuntimeMissing,
    FolderMissing,
    NoSequenceFile,
    NoEntropyTrack,
    AmbiguousSequenceFile,
    SequenceTooLarge,
    IndexFailed,
    Loading,
    Ready,
    ViewerError,
}

/// <summary>One record of the shown run: its name (the igv chromosome name), length and, for a combined-direction run, the seam base from provenance.</summary>
public sealed record ViewerContig(string Name, int Length, int? Seam);

/// <summary>
/// State for the embedded igv.js viewer page (issues #72, #73). The WebView2 host in the App layer
/// maps <see cref="MapRunFolder"/> to <c>run.deg</c>, posts every <see cref="MessageToViewer"/> string
/// to bridge.js, and forwards bridge.js events to <see cref="OnViewerMessage"/>. No WinUI reference here.
/// The <c>load</c> command is held until bridge.js reports <c>ready</c>, so it is never posted into a
/// page that is still loading, and it is kept after sending so a page reload (a new <c>ready</c>) or
/// <see cref="RetryCommand"/> redraws the same run.
/// </summary>
public sealed partial class ViewerViewModel : ObservableObject
{
    /// <summary>The <c>INavigator.NavigateTo</c> key of the viewer page; the parameter is the run's output folder (string).</summary>
    public const string PageKey = "Viewer";

    private readonly IWebViewRuntimeProbe _probe;
    private readonly IStringResourceProvider _strings;
    private readonly IEntropyOverviewSource _overviewSource;
    private readonly long _maxFastaBytes;
    private readonly IFastaIndexStore? _indexStore;
    private readonly long _maxIndexedBytes;
    private int _openGeneration;
    private CancellationTokenSource? _openCts;
    private int _contigGeneration;
    private int _fallbackGeneration;
    private bool _settingSelection;

    // The current run's load command (kept after sending, see the class comment).
    private string? _loadJson;
    private bool _pageReady;
    private bool _dark;

    // Issue #195: read from the provider each time a colour is needed, never cached.
    private readonly ITrackPaletteProvider? _palettes;
    private ViewerLocus? _locus;
    private bool _keysOn;

    // Issue #699: the locus the run was opened at (kept for a reload of the page), and the record it names.
    private string? _targetLocus;
    private string? _targetContig;

    // Status, ActionUrl and MapRunFolder are plain notifying properties, not [ObservableProperty]: no XAML
    // binds them (the page binds IsErrorVisible, ActionUri, HasLinkAction ...). Status feeds RetryCommand and
    // the tests, ActionUrl feeds ActionUri, and the host watches MapRunFolder through PropertyChanged.
    private ViewerStatus _status = ViewerStatus.Idle;
    private string? _actionUrl;
    private string? _mapRunFolder;

    [ObservableProperty]
    private bool _isViewerVisible;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isErrorVisible;

    [ObservableProperty]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorBody = string.Empty;

    /// <summary>The one action button on an error (empty label means no button): a link or a retry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkAction))]
    [NotifyPropertyChangedFor(nameof(HasRetryAction))]
    private string _actionLabel = string.Empty;

    /// <summary>The records of the shown run, for the contig selector (issue #116). Empty until the run's overview is read.</summary>
    public ObservableCollection<ViewerContig> Contigs { get; } = [];

    /// <summary>The selector is shown only when there is something to switch between (two or more records).</summary>
    [ObservableProperty]
    private bool _hasContigs;

    [ObservableProperty]
    private ViewerContig? _selectedContig;

    /// <summary>Completes when the contig list of the last <see cref="OpenRun"/> is filled (or known to be unavailable).</summary>
    public Task ContigsReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Issue #117: when the WebView2 viewer cannot start, the first record of the run's entropy track, so the viewer area still shows
    /// the track (drawn natively by the same chart as the Results page) beside the one action. Null otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFallbackChart))]
    [NotifyPropertyChangedFor(nameof(FallbackTitle))]
    private EntropyContig? _fallbackContig;

    public bool HasFallbackChart => FallbackContig is not null;

    public string FallbackTitle => FallbackContig?.Name ?? string.Empty;

    /// <summary>Completes when the fallback chart of the last failed <see cref="OpenRun"/> is filled (or known to be unavailable).</summary>
    public Task FallbackReady { get; private set; } = Task.CompletedTask;

    private string? _lastRunFolder;

    public ViewerViewModel(
        IWebViewRuntimeProbe probe,
        IStringResourceProvider strings,
        IEntropyOverviewSource overviewSource,
        long maxFastaBytes = ViewerLimits.MaxFastaBytes,
        IFastaIndexStore? indexStore = null,
        long maxIndexedBytes = ViewerLimits.MaxIndexedFastaBytes,
        ITrackPaletteProvider? palettes = null)
    {
        _palettes = palettes;
        _indexStore = indexStore;
        _maxIndexedBytes = maxIndexedBytes;
        _probe = probe;
        _strings = strings;
        _overviewSource = overviewSource;
        _maxFastaBytes = maxFastaBytes;
    }

    partial void OnSelectedContigChanged(ViewerContig? value)
    {
        // Only a choice made by the user while the page is drawn is a goto; filling the list is not.
        GoToContigCommand.NotifyCanExecuteChanged();
        if (!_settingSelection)
        {
            GoToSelectedContig();
        }
    }

    /// <summary>Jumps to the selected record again (the list sends no change when the same record is picked after panning away).</summary>
    [RelayCommand(CanExecute = nameof(CanGoToContig))]
    private void GoToContig() => GoToSelectedContig();

    private bool CanGoToContig() => SelectedContig is not null && _pageReady && Status == ViewerStatus.Ready;

    private void GoToSelectedContig()
    {
        if (SelectedContig is { } contig && _pageReady && Status == ViewerStatus.Ready)
        {
            // An explicit range: a record named like a locus ("a:1-5") must not be parsed as one.
            var locus = contig.Length > 0 ? $"{contig.Name}:1-{contig.Length}" : contig.Name;
            Send(new { cmd = "goto", locus });
        }
    }

    public ViewerStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                RetryCommand.NotifyCanExecuteChanged();
                GoToContigCommand.NotifyCanExecuteChanged();
                SyncPageKeys();
                OnPropertyChanged(nameof(AreSmoothedTracksEnabled));
            }
        }
    }

    /// <summary>Issue #552: the run's smoothed entropy tracks, each a switch (off at first: the raw track stays primary).</summary>
    public ObservableCollection<ViewerOptionalTrack> SmoothedTracks { get; } = [];

    [ObservableProperty]
    private bool _hasSmoothedTracks;

    /// <summary>Why a switched-on track is not drawn (the page could not load it): one reason, one action. Empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrackNoticeVisible))]
    private string _trackNoticeText = string.Empty;

    public bool IsTrackNoticeVisible => TrackNoticeText.Length > 0;

    /// <summary>The switches work only while the page shows the run: a track cannot be added to a page that has not drawn it.</summary>
    public bool AreSmoothedTracksEnabled => _pageReady && Status == ViewerStatus.Ready;

    private bool _settingTracks;

    private void FillSmoothedTracks(IReadOnlyList<SmoothedTrackOption> options)
    {
        SmoothedTracks.Clear();
        foreach (var o in options)
        {
            var window = o.Window.ToString(System.Globalization.CultureInfo.CurrentCulture);
            SmoothedTracks.Add(new ViewerOptionalTrack(
                o.Window,
                o.Format,
                o.Url,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("Viewer_SmoothedToggle"), window),
                string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("Viewer_SmoothedTrackName"), window),
                OnOptionalTrackChanged));
        }

        HasSmoothedTracks = SmoothedTracks.Count > 0;
        TrackNoticeText = string.Empty;
        OnPropertyChanged(nameof(AreSmoothedTracksEnabled));
    }

    // The page could not draw a track the user switched on: put the switch back to off (without telling the page) and say what to do.
    private void OnTrackFailed(string? name)
    {
        var track = SmoothedTracks.FirstOrDefault(t => t.Name == name);
        if (track is null)
        {
            return;
        }

        _settingTracks = true;
        try
        {
            track.IsOn = false;
        }
        finally
        {
            _settingTracks = false;
        }

        TrackNoticeText = string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("Viewer_TrackFailed"), track.Window);
    }

    // A freshly drawn page has none of the optional tracks, so the switches go back to off without telling the page.
    private void ResetSmoothedSwitches()
    {
        _settingTracks = true;
        try
        {
            foreach (var track in SmoothedTracks)
            {
                track.IsOn = false;
            }
        }
        finally
        {
            _settingTracks = false;
        }
    }

    // The palette is the one setting read at use time: a change in Settings shows the next time a run is opened.
    // The viewer always gets the palette's light-theme colours, in the dark theme too: dark.css turns the page dark with an invert and
    // hue-rotate filter, which keeps hue and flips lightness (TrackPalettes.ForViewer, validated through that filter).
    private PaletteColors CurrentColors() => (_palettes?.Current ?? TrackPalettes.Default).ForViewer();

    private void OnOptionalTrackChanged(ViewerOptionalTrack track)
    {
        if (_settingTracks || !AreSmoothedTracksEnabled)
        {
            return;
        }

        TrackNoticeText = string.Empty;

        if (track.IsOn)
        {
            // The same 0 to 2 bit scale as the raw track (Hard Rule 3), so the two are comparable by eye.
            Send(new
            {
                cmd = "track",
                op = "add",
                config = new { type = "wig", format = track.Format, name = track.Name, url = track.Url, min = 0, max = 2, autoscale = false, color = CurrentColors().Smoothed },
            });
        }
        else
        {
            Send(new { cmd = "track", op = "remove", name = track.Name });
        }
    }

    // The page swallows navigation keys only while the run is drawn (issue #247): a key the ViewModel would ignore must keep its
    // normal browser meaning. Sent on change only; a page that reloads starts with keys off.
    private void SyncPageKeys()
    {
        var want = _pageReady && _status == ViewerStatus.Ready;
        if (want != _keysOn)
        {
            _keysOn = want;
            if (_pageReady)
            {
                Send(new { cmd = "keys", enabled = want });
            }
        }
    }

    /// <summary>Where the error's link action goes (the WebView2 Runtime download); null for a retry or no action.</summary>
    public string? ActionUrl
    {
        get => _actionUrl;
        private set
        {
            if (SetProperty(ref _actionUrl, value))
            {
                OnPropertyChanged(nameof(ActionUri));
                OnPropertyChanged(nameof(HasLinkAction));
                OnPropertyChanged(nameof(HasRetryAction));
            }
        }
    }

    /// <summary>The folder the host must map to <c>run.deg</c> before the load command is posted; null when nothing is shown.</summary>
    public string? MapRunFolder
    {
        get => _mapRunFolder;
        private set => SetProperty(ref _mapRunFolder, value);
    }

    /// <summary>A JSON command for bridge.js (<c>load</c>, <c>theme</c>).</summary>
    public event Action<string>? MessageToViewer;

    /// <summary>An error whose one action is a link (install the WebView2 Runtime).</summary>
    public bool HasLinkAction => !string.IsNullOrEmpty(ActionLabel) && ActionUrl is not null;

    /// <summary>An error whose one action is "Try again" (<see cref="RetryCommand"/>).</summary>
    public bool HasRetryAction => !string.IsNullOrEmpty(ActionLabel) && ActionUrl is null;

    /// <summary><see cref="ActionUrl"/> as a <see cref="Uri"/> (what a XAML <c>NavigateUri</c> binds to).</summary>
    public Uri? ActionUri => ActionUrl is null ? null : new Uri(ActionUrl);

    /// <summary>
    /// The page's one entry point (issue #699): the navigation parameter is a run folder string (as before) or a <see cref="ViewerRunTarget"/>
    /// with a locus to show once the run is drawn. Anything else is "no run".
    /// </summary>
    public void OpenFromNavigation(object? parameter)
    {
        switch (parameter)
        {
            case ViewerRunTarget target:
                _targetLocus = target.Locus;
                _targetContig = target.Contig;
                OpenRun(target.RunFolder);
                break;
            case string folder:
                _targetLocus = null;
                _targetContig = null;
                OpenRun(folder);
                break;
            default:
                _targetLocus = null;
                _targetContig = null;
                OpenRun(null);
                break;
        }
    }

    /// <summary>Completes when the index of a long FASTA (issue #497) of the last <see cref="OpenRun"/> is built and applied; already complete when none was needed.</summary>
    public Task OpenReady { get; private set; } = Task.CompletedTask;

    /// <summary>The page was left: stop a running index build (issue #497). Nothing of it is applied.</summary>
    public void CancelOpen()
    {
        _openGeneration++;
        _openCts?.Cancel();
    }

    public void OpenRun(string? runFolder)
    {
        CancelOpen();
        _openGeneration++;
        OpenReady = Task.CompletedTask;
        _lastRunFolder = runFolder;
        _fallbackGeneration++; // a slow fallback read for the previous run must not land on this one
        if (string.IsNullOrWhiteSpace(runFolder))
        {
            Fail(ViewerStatus.NoRun, ViewerFailure.NoRun);
            return;
        }

        if (!WebViewRuntimeDecision.IsAvailable(_probe.GetVersion))
        {
            Fail(ViewerStatus.RuntimeMissing, ViewerFailure.RuntimeMissing, ViewerUrls.RuntimeDownloadUrl);
            FallbackReady = LoadFallbackAsync(runFolder);
            return;
        }

        var plan = IgvLoadPlanner.Plan(runFolder, _maxFastaBytes, colors: CurrentColors());
        if (plan.Failure == IgvPlanFailure.SequenceTooLarge && _indexStore is not null && IsIndexable(plan.FastaPath))
        {
            // Issue #497: a long FASTA gets a .fai in the app's own folder; building it can take seconds, so not on this thread.
            ResetRunState();
            ShowLoading();
            _openCts = new CancellationTokenSource();
            OpenReady = OpenIndexedAsync(runFolder, _openGeneration, _openCts.Token);
            return;
        }

        ApplyPlan(plan);
    }

    private bool IsIndexable(string? fastaPath) => fastaPath is not null && new FileInfo(fastaPath).Length <= _maxIndexedBytes;

    private async Task OpenIndexedAsync(string runFolder, int generation, CancellationToken cancellationToken)
    {
        IgvLoadPlan plan;
        try
        {
            var colors = CurrentColors();
            plan = await Task.Run(() => IgvLoadPlanner.PlanCancellable(runFolder, _maxFastaBytes, _indexStore, _maxIndexedBytes, cancellationToken, colors), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Never leave the page on "Loading" for a file that vanished or an unreadable folder.
            plan = new IgvLoadPlan(IgvPlanFailure.IndexFailed, runFolder, null);
        }

        if (generation == _openGeneration)
        {
            ApplyPlan(plan);
        }
    }

    private void ApplyPlan(IgvLoadPlan plan)
    {
        switch (plan.Failure)
        {
            case IgvPlanFailure.FolderMissing:
                Fail(ViewerStatus.FolderMissing, ViewerFailure.FolderMissing);
                return;
            case IgvPlanFailure.NoSequenceFile:
                Fail(ViewerStatus.NoSequenceFile, ViewerFailure.NoSequenceFile);
                return;
            case IgvPlanFailure.NoEntropyTrack:
                Fail(ViewerStatus.NoEntropyTrack, ViewerFailure.NoEntropyTrack);
                return;
            case IgvPlanFailure.AmbiguousSequenceFile:
                Fail(ViewerStatus.AmbiguousSequenceFile, ViewerFailure.AmbiguousSequence);
                return;
            case IgvPlanFailure.SequenceTooLarge:
                Fail(ViewerStatus.SequenceTooLarge, ViewerFailure.SequenceTooLarge);
                return;
            case IgvPlanFailure.IndexFailed:
                Fail(ViewerStatus.IndexFailed, ViewerFailure.IndexFailed);
                return;
        }

        FallbackContig = null;
        _loadJson = plan.LoadMessageJson;
        FillSmoothedTracks(plan.SmoothedTracks);
        // Map first: the host remaps run.deg on this change, before the load command goes out.
        MapRunFolder = plan.RunFolder;
        ShowLoading();
        SendLoadIfReady();
        ContigsReady = LoadContigsAsync(plan.RunFolder!);
    }

    // Off the UI thread, read once; a missing or malformed track just means no chart (the action card still shows).
    private async Task LoadFallbackAsync(string folder)
    {
        var generation = ++_fallbackGeneration;
        EntropyContig? first;
        try
        {
            first = (await Task.Run(() => _overviewSource.Load(folder)))?.Contigs.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            first = null;
        }

        if (generation == _fallbackGeneration)
        {
            FallbackContig = first;
        }
    }

    // The record list and seams come from the run's own entropy track and provenance (off the UI thread, read once).
    private async Task LoadContigsAsync(string folder)
    {
        var generation = ++_contigGeneration;
        ClearContigs();
        IReadOnlyList<EntropyContig> contigs;
        try
        {
            contigs = (await Task.Run(() => _overviewSource.Load(folder)))?.Contigs ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fire-and-forget: a malformed track (FormatException ...) must not fault the task unseen.
            // The viewer itself still draws the run; only the selector and seam markers are lost.
            contigs = [];
        }

        if (generation != _contigGeneration)
        {
            return;
        }

        foreach (var c in contigs)
        {
            Contigs.Add(new ViewerContig(c.Name, c.Length, c.Seam));
        }

        SelectWithoutGoto(InitialContig());
        HasContigs = Contigs.Count > 1;
        SendSeamsIfReady();
    }

    // The record the run was opened at (issue #699), else the first one.
    private ViewerContig? InitialContig()
        => (_targetContig is null ? null : Contigs.FirstOrDefault(c => c.Name == _targetContig)) ?? Contigs.FirstOrDefault();

    private void ClearContigs()
    {
        Contigs.Clear();
        SelectWithoutGoto(null);
        HasContigs = false;
    }

    private void SelectWithoutGoto(ViewerContig? contig)
    {
        _settingSelection = true;
        try
        {
            SelectedContig = contig;
        }
        finally
        {
            _settingSelection = false;
        }
    }

    // Seams are drawn in the page, so they go out once it has drawn the run and the list is known; either order works.
    private void SendSeamsIfReady()
    {
        if (!_pageReady || Status != ViewerStatus.Ready)
        {
            return;
        }

        var seams = Contigs.Where(c => c.Seam is not null).Select(c => new { chr = c.Name, pos = c.Seam!.Value }).ToArray();
        if (seams.Length > 0)
        {
            Send(new { cmd = "seams", label = _strings.GetString("ViewerSeamLabel"), color = CurrentColors().MarkerRgba(0.6), seams });
        }
    }

    /// <summary>Handles one event posted by bridge.js. Anything unrecognised, and non-fatal <c>warning</c> noise, is ignored.</summary>
    public void OnViewerMessage(string json)
    {
        string? evt;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("evt", out var e)
                || e.ValueKind != JsonValueKind.String)
            {
                return;
            }

            evt = e.GetString();
        }
        catch (JsonException)
        {
            return;
        }

        switch (evt)
        {
            case "ready":
                _pageReady = true;
                OnPropertyChanged(nameof(AreSmoothedTracksEnabled));
                _keysOn = false;
                if (_dark)
                {
                    // The page starts light; only a dark app theme needs a theme command.
                    Send(new { cmd = "theme", dark = true });
                }

                if (_loadJson is not null)
                {
                    ShowLoading();
                    SendLoadIfReady();
                }

                break;
            case "loaded":
                if (Status == ViewerStatus.Loading)
                {
                    IsLoading = false;

                    // The page reports where it landed, so the keys work before the first mouse pan (the locus listener attaches after the first draw).
                    _locus = ViewerLocus.TryParse(ReadString(json, "locus"), out var first) ? first : null;
                    ResetSmoothedSwitches();
                    Status = ViewerStatus.Ready;
                    // A freshly drawn page shows the first record; keep the selector in step with it.
                    SelectWithoutGoto(InitialContig());
                    SendSeamsIfReady();
                    if (_targetLocus is not null)
                    {
                        Send(new { cmd = "goto", locus = _targetLocus });

                        // Once: a Retry or a reloaded page must not pull the user back to this range.
                        _targetLocus = null;
                        if (Contigs.Count > 0)
                        {
                            _targetContig = null;
                        }
                    }
                }

                break;
            case "error":
                FailKeepingRun(ViewerFailure.ViewerError);
                break;
            case "locusChanged":
                _locus = ViewerLocus.TryParse(ReadString(json, "locus"), out var locus) ? locus : null;
                break;
            case "key":
                OnViewerKey(json);
                break;
            case "trackFailed":
                OnTrackFailed(ReadString(json, "name"));
                break;
        }
    }

    // Issue #247: bridge.js forwards the key; the pan, zoom and preset decisions are ViewerKeyboard's.
    private void OnViewerKey(string json)
    {
        if (!_pageReady || Status != ViewerStatus.Ready)
        {
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var key = doc.RootElement.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        var ctrl = doc.RootElement.TryGetProperty("ctrl", out var c) && c.ValueKind == JsonValueKind.True;
        int? length = _locus is { } l && Contigs.FirstOrDefault(x => x.Name == l.Chr) is { Length: > 0 } contig ? contig.Length : null;
        var result = ViewerKeyboard.Resolve(key, ctrl, _locus, length);
        switch (result.Kind)
        {
            case ViewerKeyKind.Goto:
                Send(new { cmd = "goto", locus = result.Locus });
                break;
            case ViewerKeyKind.FocusLocus:
                Send(new { cmd = "focusLocus" });
                break;
        }
    }

    private static string? ReadString(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>The WebView2 renderer or browser process died: the page is gone until the host reloads it.</summary>
    public void OnViewerProcessFailed()
    {
        _pageReady = false;
        OnPropertyChanged(nameof(AreSmoothedTracksEnabled));
        _keysOn = false;
        FailKeepingRun(ViewerFailure.ProcessFailed);
    }

    /// <summary>The WebView2 control could not start (corrupt runtime, unwritable profile folder).</summary>
    public void OnViewerInitFailed()
    {
        if (IsErrorVisible && Status != ViewerStatus.ViewerError)
        {
            // The run already failed for a more specific reason (no sequence file ...): that error is the one to fix first.
            return;
        }

        Fail(ViewerStatus.ViewerError, ViewerFailure.InitFailed);
        if (!string.IsNullOrWhiteSpace(_lastRunFolder))
        {
            FallbackReady = LoadFallbackAsync(_lastRunFolder);
        }
    }

    public void SetDarkMode(bool dark)
    {
        _dark = dark;
        if (_pageReady)
        {
            Send(new { cmd = "theme", dark });
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        ShowLoading();
        SendLoadIfReady();
    }

    private bool CanRetry() => Status == ViewerStatus.ViewerError && _pageReady && _loadJson is not null;

    private void ShowLoading()
    {
        _fallbackGeneration++;
        _locus = null;
        FallbackContig = null;
        IsErrorVisible = false;
        IsViewerVisible = true;
        IsLoading = true;
        Status = ViewerStatus.Loading;
    }

    private void SendLoadIfReady()
    {
        if (!_pageReady || _loadJson is null)
        {
            return;
        }

        MessageToViewer?.Invoke(_loadJson);
    }

    private void Send(object command) => MessageToViewer?.Invoke(JsonSerializer.Serialize(command));

    // A problem with this run (or the control): nothing to show, nothing mapped, nothing to resend.
    private void Fail(ViewerStatus status, ViewerFailure failure, string? actionUrl = null)
    {
        ResetRunState();
        ShowError(status, failure, actionUrl);
    }

    private void ResetRunState()
    {
        _loadJson = null;
        _locus = null;
        MapRunFolder = null;
        FillSmoothedTracks([]);
        _contigGeneration++;
        ClearContigs();
        FallbackContig = null;
    }

    // A failure of the viewer page itself: keep the run so a retry or a reloaded page redraws it.
    private void FailKeepingRun(ViewerFailure failure)
    {
        ShowError(ViewerStatus.ViewerError, failure, null);
        if (!string.IsNullOrWhiteSpace(_lastRunFolder))
        {
            FallbackReady = LoadFallbackAsync(_lastRunFolder);
        }
    }

    private void ShowError(ViewerStatus status, ViewerFailure failure, string? actionUrl)
    {
        var copy = ViewerCopy.For(failure);
        IsViewerVisible = false;
        IsLoading = false;
        ErrorTitle = _strings.GetString(copy.TitleKey);
        var body = _strings.GetString(copy.BodyKey);
        if (failure == ViewerFailure.SequenceTooLarge)
        {
            // The limit in force: the in-page one with no index store, the indexed one with it. Formatted from the constants (issue #497).
            body = string.Format(System.Globalization.CultureInfo.CurrentCulture, body, ViewerLimits.Describe(_indexStore is null ? _maxFastaBytes : _maxIndexedBytes));
        }

        ErrorBody = body;
        ActionUrl = actionUrl;
        ActionLabel = copy.ActionKey is null ? string.Empty : _strings.GetString(copy.ActionKey);
        IsErrorVisible = true;
        Status = status;
    }
}
