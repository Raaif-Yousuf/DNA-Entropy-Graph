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
    private readonly IStringResourceProvider _strings;
    private readonly AppDataRoot? _dataRoot;
    private readonly HashSet<string> _activeJobIds = new(StringComparer.Ordinal);

    // A plain field, not an [ObservableProperty]: no view binds the raw key (issue #490), only
    // CurrentPageTitle, which NavigateTo notifies.
    private string _currentPageKey = InitialPageKeyValue;

    [ObservableProperty]
    private string _statusPillText;

    [ObservableProperty]
    private bool _hasActiveRun;

    public ShellViewModel(INavigator navigator, IGcpAccount gcpAccount, IMessenger messenger, IStringResourceProvider strings, IDispatcher? dispatcher = null, AppDataRoot? dataRoot = null)
    {
        _dataRoot = dataRoot;
        _navigator = navigator;
        _gcpAccount = gcpAccount;
        _dispatcher = dispatcher;
        _strings = strings;
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
        _ => null,
    };

    /// <summary>The OS window title (taskbar, Alt-Tab). Reuses the plain "AppDisplayName" key because ShellTitle.Text is dotted and a code lookup of a dotted key misses.</summary>
    public string WindowTitle => HasProfileOverride
        ? _strings.GetString("AppDisplayName") + " - " + _strings.GetString("ProfileTitleSuffix")
        : _strings.GetString("AppDisplayName");

    /// <summary>True when the app runs on a data folder given by <c>--profile</c> or <c>DEG_DATA_DIR</c> (issue #638), so the window must say it is not the real profile.</summary>
    public bool HasProfileOverride => _dataRoot is { IsOverride: true };

    /// <summary>The banner text naming the sandbox folder; empty on the real profile.</summary>
    public string ProfileBannerText => _dataRoot is { IsOverride: true } root
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("ProfileBanner_Text"), root.Path)
        : string.Empty;

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        _navigator.NavigateTo(pageKey);
        _currentPageKey = pageKey;
        OnPropertyChanged(nameof(CurrentPageTitle));
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
    }

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
