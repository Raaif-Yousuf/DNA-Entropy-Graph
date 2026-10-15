using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Setup;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The first-run wizard's eight steps (Welcome, Sign in, Project, Billing,
/// APIs, Storage, Quota, Test run, Done) - docs/superpowers/specs Appendix A
/// section 2.1. This ViewModel owns steps 2 and 3 (sign in, find or make the project) and the finish; steps 4 to 7 are the OK/Fix
/// rows of <see cref="SetupStepsViewModel"/> (issue #99), which run once a project is in place.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly IGcpAccount _gcpAccount;
    private readonly IDialogService _dialogService;
    private readonly INavigator _navigator;
    private readonly IStringResourceProvider _strings;
    private readonly IDispatcher? _dispatcher;
    private readonly ISetupCheckService? _setup;
    private readonly ISettingsStore? _settings;
    private readonly IShellLauncher? _shell;

    [ObservableProperty]
    private bool _isSignedIn;

    /// <summary>What went wrong, in the user's words and naming one action (Hard Rule 13); empty when the last sign-in did not fail.</summary>
    [ObservableProperty]
    private string _signInErrorText = string.Empty;

    /// <summary>The label of the button that carries that action; empty when the message itself is the action.</summary>
    [ObservableProperty]
    private string _signInActionText = string.Empty;

    /// <summary>The project the current account has chosen, empty of meaning (null) until one is.</summary>
    [ObservableProperty]
    private string? _selectedProjectId;

    /// <summary>Why the last project choice was refused, in the user's words naming one action (Hard Rule 13); empty when it was not.</summary>
    [ObservableProperty]
    private string _projectErrorText = string.Empty;

    /// <summary>The label of the button that carries that action; empty when the message itself is the action.</summary>
    [ObservableProperty]
    private string _projectActionText = string.Empty;

    public WizardViewModel(IGcpAccount gcpAccount, IDialogService dialogService, INavigator navigator, IStringResourceProvider strings, IDispatcher? dispatcher = null, ISetupCheckService? setup = null, SetupStepsViewModel? setupSteps = null, ISettingsStore? settings = null, IShellLauncher? shell = null)
    {
        _setup = setup;
        _settings = settings;
        _shell = shell;
        SetupSteps = setupSteps;
        ProjectRow = new SetupStepRow(SetupStep.Project, "Project", strings);
        ProjectRow.ActionCommand = new AsyncRelayCommand(PerformProjectActionAsync);
        ProjectRow.RecheckCommand = new AsyncRelayCommand(ct => ContinueFromProjectAsync(ct));
        ProjectRow.PropertyChanged += (_, _) => FinishCommand.NotifyCanExecuteChanged();
        if (setupSteps is not null)
        {
            setupSteps.PropertyChanged += (_, _) => FinishCommand.NotifyCanExecuteChanged();
        }

        _strings = strings;
        _dispatcher = dispatcher;
        _gcpAccount = gcpAccount;
        _dialogService = dialogService;
        _navigator = navigator;
        _isSignedIn = gcpAccount.IsSignedIn;
        _selectedProjectId = gcpAccount.SelectedProjectId;

        // A sign-in, a switch or a sign-out changes whose project this is; the event is not guaranteed to be on the UI thread.
        // Like ShellViewModel, this lives as long as the account service, so there is nothing to unsubscribe.
        gcpAccount.AccountChanged += (_, _) => OnUiThread(RefreshAccountState);
    }

    private void RefreshAccountState()
    {
        SelectedProjectId = _gcpAccount.SelectedProjectId;
        ProjectErrorText = string.Empty;
        ProjectActionText = string.Empty;
    }

    /// <summary>Stores the project the user picked (or just created) on the current account (issue #520).</summary>
    [RelayCommand]
    private async Task ChooseProjectAsync(string? projectId, CancellationToken cancellationToken)
    {
        try
        {
            await _gcpAccount.SelectProjectAsync(projectId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (AccountAuthException failure)
        {
            OnUiThread(() =>
            {
                ProjectErrorText = _strings.GetString(AuthErrorCodes.ResourceKey(failure.Code));
                ProjectActionText = AuthErrorCodes.ActionResourceKey(failure.Code) is { } actionKey ? _strings.GetString(actionKey) : string.Empty;
            });
            return;
        }

        OnUiThread(() =>
        {
            ProjectErrorText = string.Empty;
            ProjectActionText = string.Empty;
            SelectedProjectId = _gcpAccount.SelectedProjectId;
            _ = ContinueFromProjectAsync(CancellationToken.None);
        });
    }

    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gcpAccount.SignInAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AccountAuthException failure)
        {
            // The command resumes off the UI thread (ConfigureAwait(false)), and these properties are bound to it.
            OnUiThread(() =>
            {
                SignInErrorText = _strings.GetString(AuthErrorCodes.ResourceKey(failure.Code));
                SignInActionText = AuthErrorCodes.ActionResourceKey(failure.Code) is { } actionKey ? _strings.GetString(actionKey) : string.Empty;
                IsSignedIn = _gcpAccount.IsSignedIn;
            });
            return;
        }

        OnUiThread(() =>
        {
            SignInErrorText = string.Empty;
            SignInActionText = string.Empty;
            IsSignedIn = _gcpAccount.IsSignedIn;
            SelectedProjectId = _gcpAccount.SelectedProjectId;
            if (IsSignedIn)
            {
                _ = ContinueFromProjectAsync(CancellationToken.None);
            }
        });
    }

    /// <summary>The OK/Fix rows of steps 4 to 7; null in a wizard built without them (the sign-in tests).</summary>
    public SetupStepsViewModel? SetupSteps { get; }

    /// <summary>Step 3 as a row: green once the chosen project is checked, red with the list (or the make-one button) until then.</summary>
    public SetupStepRow ProjectRow { get; }

    /// <summary>The projects the account can use, this app's own first; shown only while the user has to choose.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProjectChoice> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    /// <summary>Checks the chosen project and, if it is fine, the steps after it; else asks the user to choose or make one.</summary>
    private async Task ContinueFromProjectAsync(CancellationToken cancellationToken)
    {
        if (_setup is null)
        {
            return;
        }

        SetProjects([]);
        if (_gcpAccount.SelectedProjectId is { Length: > 0 } project)
        {
            ProjectRow.MarkRunning();
            var checkedProject = await _setup.CheckProjectAsync(project, cancellationToken);
            ProjectRow.Apply(checkedProject, project);
            if (checkedProject.IsOk)
            {
                await RunRemainingStepsAsync(cancellationToken);
                return;
            }
        }
        else
        {
            ProjectRow.MarkRunning();
        }

        await ShowProjectListAsync(cancellationToken);
    }

    private async Task ShowProjectListAsync(CancellationToken cancellationToken)
    {
        if (_setup is null)
        {
            return;
        }

        var listed = await _setup.ListProjectsAsync(cancellationToken);
        if (!listed.IsOk)
        {
            ProjectRow.Apply(listed, string.Empty);
            return;
        }

        // A failed check above keeps its own message (the project cannot be used); the list only adds the choices.
        if (!ProjectRow.IsFix)
        {
            ProjectRow.MarkChoose(listed.Projects.Count > 0 ? "WizardProject_Pick" : "WizardProject_None");
        }

        SetProjects(listed.Projects);
    }

    private void SetProjects(IEnumerable<ProjectSummary> projects)
    {
        Projects.Clear();
        foreach (var project in projects)
        {
            var id = project.ProjectId;
            Projects.Add(new ProjectChoice(id, project.DisplayName, project.IsAppProject, new AsyncRelayCommand(() => ChooseProjectCommand.ExecuteAsync(id))));
        }

        OnPropertyChanged(nameof(HasProjects));
    }

    private Task RunRemainingStepsAsync(CancellationToken cancellationToken)
        => SetupSteps is null ? Task.CompletedTask : SetupSteps.RunAllCommand.ExecuteAsync(null);

    /// <summary>Makes a project of the user's own (named DNA Entropy Graph, labelled for this app and this installation) and goes on with it.</summary>
    [RelayCommand]
    private async Task CreateProjectAsync(CancellationToken cancellationToken)
    {
        if (_setup is null)
        {
            return;
        }

        var id = ProjectIdGenerator.Generate();
        var installation = _settings is null ? string.Empty : InstallationId.GetOrCreate(_settings);
        ProjectRow.MarkRunning();
        var made = await _setup.CreateProjectAsync(id, installation, cancellationToken);
        if (!made.IsOk)
        {
            ProjectRow.Apply(made, id);
            await ShowProjectListAsync(cancellationToken);
            return;
        }

        await ChooseProjectCommand.ExecuteAsync(made.ProjectId ?? id);
    }

    private async Task PerformProjectActionAsync(CancellationToken cancellationToken)
    {
        switch (ProjectRow.ActionKind)
        {
            case SetupActionKind.CopyText when _shell is not null:
                _shell.CopyText(ProjectRow.RequestText);
                break;
            case SetupActionKind.CreateProject:
                await CreateProjectCommand.ExecuteAsync(null);
                break;
            default:
                await ContinueFromProjectAsync(cancellationToken);
                break;
        }
    }

    /// <summary>Done: every step is green (or the GPU allowance was skipped), so go and make the first run.</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish() => _navigator.NavigateTo("NewRun");

    private bool CanFinish => ProjectRow.IsOk && SetupSteps is { IsAllDone: true };

    private void OnUiThread(Action action)
    {
        if (_dispatcher is null)
        {
            action();
            return;
        }

        _dispatcher.Enqueue(action);
    }
}

/// <summary>One project the user can pick, with the command that chooses it (a command on the item, so the list needs no code-behind).</summary>
public sealed record ProjectChoice(string ProjectId, string DisplayName, bool IsAppProject, IAsyncRelayCommand ChooseCommand);
