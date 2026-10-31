using System.Collections.ObjectModel;
using System.Data.Common;
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

    public ProjectSwitchSettingsViewModel(ProjectSwitcher switcher, IGcpAccount account, IStringResourceProvider strings)
    {
        _switcher = switcher;
        _account = account;
        _strings = strings;
    }

    public ObservableCollection<string> ProjectIds { get; } = [];

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Called without anyone awaiting it (the page opening): a failure ends up in Status, never as an unobserved exception.
        IReadOnlyList<ProjectSettings> all;
        try
        {
            all = await _switcher.ListAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbException or UnauthorizedAccessException)
        {
            Status = _strings.GetString("ProjectListFailed");
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

    private string WithAction(string message, string? actionKey) => actionKey is null ? message : message + " " + _strings.GetString(actionKey);

    private string BucketLine(string? projectId)
        => projectId is not null && _known.TryGetValue(projectId, out var settings) && !string.IsNullOrWhiteSpace(settings.Bucket)
            ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("ProjectBucketKnown"), settings.Bucket)
            : _strings.GetString("ProjectBucketNone");
}
