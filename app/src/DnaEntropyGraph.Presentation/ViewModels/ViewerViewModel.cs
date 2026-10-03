using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    Loading,
    Ready,
    ViewerError,
}

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
    private readonly long _maxFastaBytes;

    // The current run's load command (kept after sending, see the class comment).
    private string? _loadJson;
    private bool _pageReady;
    private bool _dark;

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

    public ViewerViewModel(IWebViewRuntimeProbe probe, IStringResourceProvider strings, long maxFastaBytes = ViewerLimits.MaxFastaBytes)
    {
        _probe = probe;
        _strings = strings;
        _maxFastaBytes = maxFastaBytes;
    }

    public ViewerStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                RetryCommand.NotifyCanExecuteChanged();
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

    public void OpenRun(string? runFolder)
    {
        if (string.IsNullOrWhiteSpace(runFolder))
        {
            Fail(ViewerStatus.NoRun, ViewerFailure.NoRun);
            return;
        }

        if (!WebViewRuntimeDecision.IsAvailable(_probe.GetVersion))
        {
            Fail(ViewerStatus.RuntimeMissing, ViewerFailure.RuntimeMissing, ViewerUrls.RuntimeDownloadUrl);
            return;
        }

        var plan = IgvLoadPlanner.Plan(runFolder, _maxFastaBytes);
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
        }

        _loadJson = plan.LoadMessageJson;
        // Map first: the host remaps run.deg on this change, before the load command goes out.
        MapRunFolder = plan.RunFolder;
        ShowLoading();
        SendLoadIfReady();
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
                    Status = ViewerStatus.Ready;
                }

                break;
            case "error":
                FailKeepingRun(ViewerFailure.ViewerError);
                break;
        }
    }

    /// <summary>The WebView2 renderer or browser process died: the page is gone until the host reloads it.</summary>
    public void OnViewerProcessFailed()
    {
        _pageReady = false;
        FailKeepingRun(ViewerFailure.ProcessFailed);
    }

    /// <summary>The WebView2 control could not start (corrupt runtime, unwritable profile folder).</summary>
    public void OnViewerInitFailed() => Fail(ViewerStatus.ViewerError, ViewerFailure.InitFailed);

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
        _loadJson = null;
        MapRunFolder = null;
        ShowError(status, failure, actionUrl);
    }

    // A failure of the viewer page itself: keep the run so a retry or a reloaded page redraws it.
    private void FailKeepingRun(ViewerFailure failure) => ShowError(ViewerStatus.ViewerError, failure, null);

    private void ShowError(ViewerStatus status, ViewerFailure failure, string? actionUrl)
    {
        var copy = ViewerCopy.For(failure);
        IsViewerVisible = false;
        IsLoading = false;
        ErrorTitle = _strings.GetString(copy.TitleKey);
        ErrorBody = _strings.GetString(copy.BodyKey);
        ActionUrl = actionUrl;
        ActionLabel = copy.ActionKey is null ? string.Empty : _strings.GetString(copy.ActionKey);
        IsErrorVisible = true;
        Status = status;
    }
}
