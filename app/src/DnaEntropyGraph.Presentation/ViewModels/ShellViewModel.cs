using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The app shell: nav rail + current page host (docs/superpowers/specs
/// Appendix A section 2). Two things this ViewModel owns beyond navigation,
/// both named as this lane's own wired-to-nothing observable in the brief:
/// <see cref="StatusPillText"/> (issue #62's "the pill reads 'Not signed
/// in'" on a fresh profile) and <see cref="HasActiveRun"/> (the Runs nav
/// item's badge, driven by <see cref="RunPhaseChangedMessage"/> so Shell
/// never needs a direct reference to the App-layer JobEngine that sends it -
/// docs/architecture.md section 3).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>The destination the shell selects at startup (issue #490); also the page whose title <see cref="CurrentPageTitle"/> shows first.</summary>
    public const string InitialPageKeyValue = "NewRun";

    public static string InitialPageKey => InitialPageKeyValue;

    private readonly INavigator _navigator;
    private readonly IGcpAccount _gcpAccount;
    private readonly IDispatcher? _dispatcher;
    private readonly RunCompletionActions _completion;
    private readonly IStringResourceProvider _strings;
    private readonly AppDataRoot? _dataRoot;
    private readonly SimulatedCloudMode? _simulatedCloud;
    private readonly BuildIdentity? _build;
    private readonly SessionRestorer? _session;
    private readonly PrototypeImportViewModel? _prototypeImport;
    private SessionTarget? _pendingRestore;
    private bool _userNavigated;
    private readonly HashSet<string> _activeJobIds = new(StringComparer.Ordinal);

    // A plain field, not an [ObservableProperty]: no view binds the raw key (issue #490), only
    // CurrentPageTitle, which NavigateTo notifies.
    private string _currentPageKey = InitialPageKeyValue;

    [ObservableProperty]
    private string _statusPillText;

    [ObservableProperty]
    private bool _hasActiveRun;

    /// <summary>The in-app message bar state (issue #585): the InfoBar in MainWindow binds to this, and it is the same instance the DI-registered <c>IToastService</c> writes to.</summary>
    public InAppMessageCenter Messages { get; }

    /// <summary>The update check state (issue #179): the shell's Update available bar binds to it, and Settings > About shows the same instance. Null only in tests that do not exercise updates.</summary>
    public UpdateViewModel? Updates { get; }

    public ShellViewModel(INavigator navigator, IGcpAccount gcpAccount, IMessenger messenger, IStringResourceProvider strings, InAppMessageCenter messages, RunCompletionActions completion, IDispatcher? dispatcher = null, AppDataRoot? dataRoot = null, SimulatedCloudMode? simulatedCloud = null, UpdateViewModel? updates = null, BuildIdentity? build = null, SessionRestorer? session = null, PrototypeImportViewModel? prototypeImport = null)
    {
        _prototypeImport = prototypeImport;
        _session = session;
        Updates = updates;
        _build = build;
        _completion = completion;
        _dataRoot = dataRoot;
        _simulatedCloud = simulatedCloud;
        _navigator = navigator;
        _gcpAccount = gcpAccount;
        _dispatcher = dispatcher;
        _strings = strings;
        Messages = messages;
        _statusPillText = BuildStatusPillText(gcpAccount, strings);

        messenger.Register<ShellViewModel, RunPhaseChangedMessage>(this, static (recipient, message) => recipient.OnRunPhaseChanged(message));

        // Issue #48: sign-in, sign-out and an account switch change the pill. The shell lives as long as the app, so no unsubscribe.
        gcpAccount.AccountChanged += (_, _) => RefreshStatusPill();
    }

    /// <summary>The account service raises its event from whatever thread finished the sign-in; the pill is bound to the UI, so the update goes through the dispatcher when there is one.</summary>
    private void RefreshStatusPill()
    {
        if (_dispatcher is null)
        {
            StatusPillText = BuildStatusPillText(_gcpAccount, _strings);
            return;
        }

        _dispatcher.Enqueue(() => StatusPillText = BuildStatusPillText(_gcpAccount, _strings));
    }

    /// <summary>The NavigationView header: the localized page name (issue #490), never the raw page key; empty for a page with no title key.</summary>
    public string CurrentPageTitle => PageTitleKey(_currentPageKey) is { } key ? _strings.GetString(key) : string.Empty;

    /// <summary>
    /// The plain (non-dotted, see the note below) resw key for a nav destination's header. Literal keys on
    /// purpose: scripts/check_app_wiring.py's ORPHAN-RESOURCE scan, and a grep, see only literal names.
    /// </summary>
    public static string? PageTitleKey(string pageKey) => pageKey switch
    {
        "NewRun" => "PageTitle_NewRun",
        "Runs" => "PageTitle_Runs",
        "Cloud" => "PageTitle_Cloud",
        "Settings" => "PageTitle_Settings",
        "Wizard" => "PageTitle_Wizard",
        _ => null,
    };

    /// <summary>The OS window title (taskbar, Alt-Tab). Reuses the plain "AppDisplayName" key because ShellTitle.Text is dotted and a code lookup of a dotted key misses.</summary>
    public string WindowTitle => HasProfileOverride
        ? _strings.GetString("AppDisplayName") + " - " + _strings.GetString("ProfileTitleSuffix")
        : _strings.GetString("AppDisplayName");

    /// <summary>True when the app runs on a data folder given by <c>--profile</c> or <c>DEG_DATA_DIR</c> (issue #638), so the window must say it is not the real profile.</summary>
    /// <summary>True when the app runs over the simulated cloud (issue #69): the banner is permanent, never dismissible.</summary>
    /// <summary>What the status pill does, for a screen reader: it opens the setup wizard (issue #99).</summary>
    public string StatusPillHelp => _strings.GetString("StatusPillHelp");

    /// <summary>True for an unsigned build (issue #263): a development build or a CI artifact, never a release, so the title bar says so.</summary>
    public bool HasDevBuildBanner => _build is { IsDevBuild: true };

    public string DevBuildBannerText => _build is { IsDevBuild: true } build
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("DevBuild_Banner"), build.Info.Display)
        : string.Empty;

    public bool HasSimulatedCloud => _simulatedCloud is not null;

    public string SimulatedCloudBannerText => _simulatedCloud is null ? string.Empty : _strings.GetString("SimulatedCloud_Banner");

    public bool HasProfileOverride => _dataRoot is { IsOverride: true };

    /// <summary>The banner text naming the sandbox folder; empty on the real profile.</summary>
    public string ProfileBannerText => _dataRoot is { IsOverride: true } root
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("ProfileBanner_Text"), root.Path)
        : string.Empty;

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        _userNavigated = true;
        _navigator.NavigateTo(pageKey);
        _currentPageKey = pageKey;
        OnPropertyChanged(nameof(CurrentPageTitle));

        // Issue #244: the first time the remembered page's parent is shown at startup, open the remembered run on top of it. Once only,
        // and only if the user has not already chosen another page (that choice replaced the pending restore).
        var pending = _pendingRestore;
        _pendingRestore = null;
        if (pending is { SubPage: not null } && pending.NavKey == pageKey)
        {
            _navigator.NavigateTo(pending.SubPage, pending.Parameter);
        }
    }

    /// <summary>First-launch offers, run once the window is showing (issue #243: bring in the earlier version's runs). Never throws.</summary>
    public async Task RunStartupOffersAsync(CancellationToken cancellationToken)
    {
        if (_prototypeImport is not null)
        {
            await _prototypeImport.OfferOnFirstLaunchAsync(cancellationToken);
        }
    }

    /// <summary>The navigation item the shell selects at startup: the remembered page's (issue #244), else New run; null when the user already chose a page. Valid after <see cref="PrepareSessionAsync"/>.</summary>
    public string? StartupNavKey { get; private set; } = InitialPageKeyValue;

    /// <summary>How long startup waits for the remembered page to resolve before opening on New run (issue #244). Tests shorten it.</summary>
    public TimeSpan SessionRestoreTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads where the user left off (issue #244) before the first navigation. Never throws and never waits longer than
    /// <see cref="SessionRestoreTimeout"/>: a locked or hung history means "start on New run". A page the user already chose wins over the restore.
    /// </summary>
    public async Task PrepareSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        SessionTarget? target = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // Task.Run so a settings read that blocks synchronously is bounded too, not only an awaited history read.
            target = await Task.Run(() => _session.ResolveAsync(limit.Token), limit.Token).WaitAsync(SessionRestoreTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            limit.Cancel();
            System.Diagnostics.Trace.TraceWarning("session restore timed out; opening on the default page");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning($"session restore failed: {ex.GetType().Name}");
        }

        if (_userNavigated)
        {
            // The user went to a page while this was resolving (a nav item, or one with none such as the setup wizard): that choice stands,
            // nothing is restored on top of it, and null tells the window to leave the selection alone.
            StartupNavKey = null;
            return;
        }

        _pendingRestore = target;
        StartupNavKey = target?.NavKey ?? InitialPageKeyValue;
    }

    /// <summary>Alt+Left (issue #110): back to the previous page; nothing when there is none.</summary>
    public void GoBack()
    {
        if (_navigator.CanGoBack)
        {
            _navigator.GoBack();
        }
    }

    private void OnRunPhaseChanged(RunPhaseChangedMessage message)
    {
        if (IsTerminal(message.Phase))
        {
            _activeJobIds.Remove(message.JobId);
        }
        else
        {
            _activeJobIds.Add(message.JobId);
        }

        HasActiveRun = _activeJobIds.Count > 0;

        // Issue #643: what happens at the end of a run (toast, open folder, open Results) is decided by the Notifications settings.
        if (message.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted)
        {
            CompletionWork = _completion.OnRunFinishedAsync(message.JobId, message.Phase);
        }
        else if (message.Phase == JobPhase.Failed)
        {
            // Issue #107: a failed run is worth a Windows notification too (desktop only; the run's own page says why).
            CompletionWork = _completion.OnRunFailedAsync(message.JobId);
        }
    }

    /// <summary>The last run-finished work started by <see cref="OnRunPhaseChanged"/>; tests await it.</summary>
    public Task CompletionWork { get; private set; } = Task.CompletedTask;

    private static bool IsTerminal(JobPhase phase) => phase is JobPhase.Completed or JobPhase.PartiallyCompleted or JobPhase.Cancelled or JobPhase.Failed;

    // Keys are the plain, non-dotted resw names ("StatusPillSignedIn", not
    // "StatusPillSignedIn.Text"): a .resw entry named "Foo.Text" compiles
    // into the PRI as the nested resource path "Foo/Text", which is exactly
    // what x:Uid's own "Uid.Property" convention relies on for a XAML
    // binding. A code call through ResourceLoader.GetString(key) has no such
    // convention - it looks up the literal key string, so a dotted key here
    // misses the compiled path and ReswStringResourceProvider's not-found
    // fallback returns the key itself (MEASURED: the title bar showed the
    // literal text "StatusPillSignedIn.Text"). Only keys that are also
    // looked up via x:Uid (ShellTitle.Text, NavNewRun.Content, ...) keep the
    // dotted form; every key reached exclusively from code stays plain, the
    // same convention the Phase*_Title keys already use.
    private static string BuildStatusPillText(IGcpAccount gcpAccount, IStringResourceProvider strings)
        => gcpAccount.IsSignedIn
            ? string.Format(strings.GetString("StatusPillSignedIn"), gcpAccount.CurrentAccount?.Email ?? gcpAccount.SelectedProjectId)
            : strings.GetString("StatusPillNotSignedIn");
}
