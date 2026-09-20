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
    private readonly INavigator _navigator;
    private readonly HashSet<string> _activeJobIds = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _currentPageKey = "NewRun";

    [ObservableProperty]
    private string _statusPillText;

    [ObservableProperty]
    private bool _hasActiveRun;

    public ShellViewModel(INavigator navigator, IGcpAccount gcpAccount, IMessenger messenger, IStringResourceProvider strings)
    {
        _navigator = navigator;
        _statusPillText = BuildStatusPillText(gcpAccount, strings);

        messenger.Register<ShellViewModel, RunPhaseChangedMessage>(this, static (recipient, message) => recipient.OnRunPhaseChanged(message));
    }

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        _navigator.NavigateTo(pageKey);
        CurrentPageKey = pageKey;
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
            ? string.Format(strings.GetString("StatusPillSignedIn"), gcpAccount.SelectedProjectId)
            : strings.GetString("StatusPillNotSignedIn");
}
