using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "Google Cloud project" group (issue #260): the projects this account knows, a Switch button that points the signed-in
/// account at another one without a browser, and the bucket remembered for the chosen project. A switch never touches a run that is already
/// going: every call on a run uses the run's own project (issue #830, see <see cref="ProjectSwitcher"/>).
/// </summary>
public sealed partial class ProjectSwitchSettingsViewModel : ObservableObject
{
    private readonly ProjectSwitcher _switcher;
    private readonly IGcpAccount _account;
    private readonly IStringResourceProvider _strings;
    private Dictionary<string, ProjectSettings> _known = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    private string? _chosenProjectId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    private string? _currentProjectId;

    [ObservableProperty]
    private string _bucketText = string.Empty;

    /// <summary>The plain-words result of the last switch, or the named failure and its action. Empty before the first one.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    private readonly IDispatcher? _dispatcher;
    private int _refreshGeneration;

    public ProjectSwitchSettingsViewModel(ProjectSwitcher switcher, IGcpAccount account, IStringResourceProvider strings, IDispatcher? dispatcher = null)
    {
        _switcher = switcher;
        _account = account;
        _strings = strings;
        _dispatcher = dispatcher;
        // The list belongs to the signed-in account: another account becoming the active one (switched elsewhere, signed out, signed in) must not leave this one on screen.
        _account.AccountChanged += OnAccountChanged;
    }

    /// <summary>
    /// Ends the subscription to <see cref="IGcpAccount.AccountChanged"/>. The account is a singleton and this view model is transient, so without it every visit to Settings
    /// would leave one more view model reachable and reacting. The page calls it when it is navigated away from.
    /// </summary>
    public void Detach() => _account.AccountChanged -= OnAccountChanged;

    public ObservableCollection<string> ProjectIds { get; } = [];

    /// <summary>
    /// The one way to start a refresh nobody awaits (the page opening, the account changing). It observes every failure: cancellation is the end of the work, anything
    /// else is logged by class and shown as the list-failed message, so no exception is left on a task nobody reads.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The work was cancelled: not a failure to report.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"project list refresh failed unexpectedly: {ex.GetType().Name}");
            Status = _strings.GetString("ProjectListFailed");
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is null)
        {
            StartAccountRefresh();
            return;
        }

        _dispatcher.Enqueue(StartAccountRefresh);
    }

    /// <summary>"Switched to X" (or a failure) belongs to the previous account's card: clear it, then list for the account that is active now.</summary>
    private void StartAccountRefresh()
    {
        Status = string.Empty;
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => RefreshCoreAsync(cancellationToken);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        // Called without anyone awaiting it (the page opening): a failure ends up in Status, never as an unobserved exception.
        var generation = Interlocked.Increment(ref _refreshGeneration);
        IReadOnlyList<ProjectSettings> all;
        try
        {
            all = await _switcher.ListAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever the store or the run history threw (a disk, a locked database, anything unforeseen): one message with its action, never an exception on a task nobody reads.
            System.Diagnostics.Trace.TraceWarning($"project list failed: {ex.GetType().Name}");
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                // Only the newest refresh reports: an older one failing must not overwrite what the newer one is about to show.
                Status = _strings.GetString("ProjectListFailed");
            }

            return;
        }

        if (generation != Volatile.Read(ref _refreshGeneration))
        {
            // A newer refresh (the account changed while this one was reading) owns the list now; showing this older answer would put the old account's projects back.
            return;
        }

        _known = all.ToDictionary(p => p.ProjectId, StringComparer.Ordinal);
        ProjectIds.Clear();
        foreach (var project in all)
        {
            ProjectIds.Add(project.ProjectId);
        }

        CurrentProjectId = _account.SelectedProjectId;
        ChosenProjectId = CurrentProjectId;
        BucketText = BucketLine(CurrentProjectId);
    }

    private bool CanSwitch() => !string.IsNullOrWhiteSpace(ChosenProjectId) && ChosenProjectId != CurrentProjectId;

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private async Task SwitchAsync(CancellationToken cancellationToken)
    {
        if (ChosenProjectId is not { } target)
        {
            return;
        }

        try
        {
            var settings = await _switcher.SwitchAsync(target, cancellationToken).ConfigureAwait(true);
            _known[target] = settings;
            CurrentProjectId = target;
            BucketText = BucketLine(target);
            Status = string.Format(CultureInfo.CurrentCulture, _strings.GetString("ProjectSwitched"), target);
        }
        catch (AccountAuthException failure)
        {
            // Every refusal leaves the current project as it was (the switcher selects last), so the card and the account still agree.
            Status = WithAction(_strings.GetString(AuthErrorCodes.ResourceKey(failure.Code)), AuthErrorCodes.ActionResourceKey(failure.Code));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The command runs from a button, so an exception left here would crash the page or vanish. Whatever it was, the person gets one message and one action.
            System.Diagnostics.Trace.TraceWarning($"project switch failed unexpectedly: {ex.GetType().Name}");
            Status = WithAction(_strings.GetString("ProjectSwitchFailed"), "AuthAction_TryAgain");
        }
    }

    /// <summary>
    /// The message, then the one action as a plain phrase. Some shared messages already say their action in words ("..., then choose Try again."; the wizard shows the same
    /// text beside a real button), and the status line would then read it twice, so the phrase is appended only when the message does not already contain it.
    /// </summary>
    private string WithAction(string message, string? actionKey)
    {
        if (actionKey is null)
        {
            return message;
        }

        var action = _strings.GetString(actionKey);
        return message.Contains(action, StringComparison.Ordinal) ? message : message + " " + action;
    }

    private string BucketLine(string? projectId)
        => projectId is not null && _known.TryGetValue(projectId, out var settings) && !string.IsNullOrWhiteSpace(settings.Bucket)
            ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("ProjectBucketKnown"), settings.Bucket)
            : _strings.GetString("ProjectBucketNone");
}
