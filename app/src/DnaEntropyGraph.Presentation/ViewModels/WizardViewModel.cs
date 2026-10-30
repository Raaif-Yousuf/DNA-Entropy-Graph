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
    [NotifyPropertyChangedFor(nameof(IsNotSignedIn))]
    [NotifyPropertyChangedFor(nameof(ShowSignInButton))]
    private bool _isSignedIn;

    public bool IsNotSignedIn => !IsSignedIn;

    /// <summary>
    /// Issue #624: the saved account list could not be read at launch (another program or copy of the app holds it), so "not signed in" would be
    /// a guess. The card shows <see cref="AccountsFileLockedText"/> and a Try again button instead of Sign in.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInButton))]
    private bool _isAccountsFileLocked;

    /// <summary>The ACCOUNTS_FILE_LOCKED message, in the user's words naming one action; empty unless <see cref="IsAccountsFileLocked"/>.</summary>
    [ObservableProperty]
    private string _accountsFileLockedText = string.Empty;

    /// <summary>The label of the Try again button for the locked list; empty unless <see cref="IsAccountsFileLocked"/>.</summary>
    [ObservableProperty]
    private string _accountsFileRetryText = string.Empty;

    /// <summary>Sign in is offered when signed out and the list could be read; with the list locked, Try again is the action (signing in would not help and may be unneeded).</summary>
    public bool ShowSignInButton => IsNotSignedIn && !IsAccountsFileLocked;

    /// <summary>What went wrong, in the user's words and naming one action (Hard Rule 13); empty when the last sign-in did not fail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInError))]
    private string _signInErrorText = string.Empty;

    public bool HasSignInError => SignInErrorText.Length > 0;

    /// <summary>The label of the button that carries that action; empty when the message itself is the action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInAction))]
    private string _signInActionText = string.Empty;

    public bool HasSignInAction => SignInActionText.Length > 0;

    /// <summary>The project the current account has chosen, empty of meaning (null) until one is.</summary>
    [ObservableProperty]
    private string? _selectedProjectId;

    /// <summary>Why the last project choice was refused, in the user's words naming one action (Hard Rule 13); empty when it was not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectError))]
    private string _projectErrorText = string.Empty;

    public bool HasProjectError => ProjectErrorText.Length > 0;

    /// <summary>The label of the button that carries that action; empty when the message itself is the action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectAction))]
    private string _projectActionText = string.Empty;

    public bool HasProjectAction => ProjectActionText.Length > 0;

    /// <summary>"Copied" or why the clipboard would not take the project request (with the action); empty until the button is pressed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectNotice))]
    private string _projectNoticeText = string.Empty;

    public bool HasProjectNotice => ProjectNoticeText.Length > 0;

    /// <summary>The project check, the list or a project being made is in flight: Finish waits.</summary>
    private bool _isFlowBusy;

    private void SetFlowBusy(bool busy)
    {
        _isFlowBusy = busy;
        FinishCommand.NotifyCanExecuteChanged();
    }

    // One project flow at a time (a sign-in, a pick, a re-check and "pick another" all start one), and one token that an account change cancels.
    private readonly SemaphoreSlim _flowGate = new(1, 1);
    private CancellationTokenSource _flowCancellation = new();
    private Task _flow = Task.CompletedTask;

    /// <summary>Completes when the project flow started by the last sign-in or choice has ended (it is never fire-and-forget: a failure shows on the project row).</summary>
    public Task WhenIdleAsync() => _flow;

    public WizardViewModel(IGcpAccount gcpAccount, IDialogService dialogService, INavigator navigator, IStringResourceProvider strings, IDispatcher? dispatcher = null, ISetupCheckService? setup = null, SetupStepsViewModel? setupSteps = null, ISettingsStore? settings = null, IShellLauncher? shell = null)
    {
        _setup = setup;
        _settings = settings;
        _shell = shell;
        SetupSteps = setupSteps;
        ProjectRow = new SetupStepRow(SetupStep.Project, strings);
        ProjectRow.ActionCommand = new AsyncRelayCommand(() => ObservedAsync(PerformProjectActionAsync));
        ProjectRow.RecheckCommand = new AsyncRelayCommand(() => ObservedAsync(ContinueFromProjectAsync));
        ProjectRow.PropertyChanged += (_, _) => FinishCommand.NotifyCanExecuteChanged();
        if (setupSteps is not null)
        {
            setupSteps.PropertyChanged += (_, _) => FinishCommand.NotifyCanExecuteChanged();
            setupSteps.ProjectActionHandler = (kind, _) => ObservedAsync(ct => kind == SetupActionKind.CreateProject ? CreateProjectCommand.ExecuteAsync(null) : PickAnotherProjectAsync(ct));
        }

        _strings = strings;
        _dispatcher = dispatcher;
        _gcpAccount = gcpAccount;
        _dialogService = dialogService;
        _navigator = navigator;
        _isSignedIn = gcpAccount.IsSignedIn;
        _selectedProjectId = gcpAccount.SelectedProjectId;
        ApplyAccountsFileState(gcpAccount.IsAccountsFileLocked);

        // A sign-in, a switch or a sign-out changes whose project this is; the event is not guaranteed to be on the UI thread.
        // Like ShellViewModel, this lives as long as the account service, so there is nothing to unsubscribe.
        gcpAccount.AccountChanged += (_, _) => OnUiThread(RefreshAccountState);
    }

    private void RefreshAccountState()
    {
        // Whose project this is has changed: checks still running are for the old one, so they stop (and leave no row "Checking...").
        var old = _flowCancellation;
        _flowCancellation = new CancellationTokenSource();
        old.Cancel();
        SelectedProjectId = _gcpAccount.SelectedProjectId;
        ProjectErrorText = string.Empty;
        ProjectActionText = string.Empty;
        var wasLocked = IsAccountsFileLocked;
        ApplyAccountsFileState(_gcpAccount.IsAccountsFileLocked);
        if (wasLocked && !IsAccountsFileLocked)
        {
            // The list can be read now: show what it holds.
            IsSignedIn = _gcpAccount.IsSignedIn;
        }
    }

    private void ApplyAccountsFileState(bool locked)
    {
        IsAccountsFileLocked = locked;
        AccountsFileLockedText = locked ? _strings.GetString(AuthErrorCodes.ResourceKey(AuthErrorCodes.AccountsFileLocked)) : string.Empty;
        AccountsFileRetryText = locked && AuthErrorCodes.ActionResourceKey(AuthErrorCodes.AccountsFileLocked) is { } actionKey ? _strings.GetString(actionKey) : string.Empty;
    }

    /// <summary>Try again for a locked account list: reads it again (the service raises AccountChanged if it can be read now) and never opens the browser.</summary>
    [RelayCommand]
    private void RetryAccountsFile() => _gcpAccount.RecheckAccountsFile();

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
            StartProjectFlow();
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
                StartProjectFlow();
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
    private Task ContinueFromProjectAsync(CancellationToken cancellationToken) => ExclusiveAsync(cancellationToken, async () =>
    {
        if (_setup is null)
        {
            return;
        }

        ProjectNoticeText = string.Empty;
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
    });

    /// <summary>"Pick an existing project": shows the list. It does not check the project that was just refused again.</summary>
    private Task PickAnotherProjectAsync(CancellationToken cancellationToken) => ExclusiveAsync(cancellationToken, async () =>
    {
        if (_setup is null)
        {
            return;
        }

        if (!ProjectRow.IsFix)
        {
            ProjectRow.MarkChoose("WizardProject_Pick");
        }

        await ShowProjectListAsync(cancellationToken);
    });

    /// <summary>One project flow at a time; the gate is always given back and no row is left on "Checking..." (a cancelled flow, an error).</summary>
    private async Task ExclusiveAsync(CancellationToken cancellationToken, Func<Task> work)
    {
        await _flowGate.WaitAsync(cancellationToken);
        SetFlowBusy(true);
        try
        {
            await work();
        }
        finally
        {
            if (ProjectRow.IsRunning)
            {
                ProjectRow.MarkPending();
            }

            SetFlowBusy(false);
            _flowGate.Release();
        }
    }

    /// <summary>
    /// Runs a project-flow step so that nothing escapes it: cancellation ends it quietly, and any other failure becomes the project row's
    /// catch-all message with "Try again" (an unobserved exception would be silent, or end the app).
    /// </summary>
    private async Task ObservedAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            await work(_flowCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ProjectRow.Apply(new SetupStepResult(SetupStep.Project, SetupStatus.NeedsAction) { Action = new SetupAction(SetupActionKind.Retry, "SetupAction_TryAgain") }, _gcpAccount.SelectedProjectId ?? string.Empty);
        }
    }

    private void StartProjectFlow() => _flow = ObservedAsync(ContinueFromProjectAsync);

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
        => SetupSteps is null ? Task.CompletedTask : SetupSteps.RunAllAsync(cancellationToken);

    /// <summary>Makes a project of the user's own (named DNA Entropy Graph, labelled for this app and this installation) and goes on with it.</summary>
    [RelayCommand]
    private Task CreateProjectAsync() => ObservedAsync(MakeProjectAsync);

    private async Task MakeProjectAsync(CancellationToken cancellationToken)
    {
        if (_setup is null)
        {
            return;
        }

        var id = ProjectIdGenerator.Generate();
        var installation = _settings is null ? string.Empty : InstallationId.GetOrCreate(_settings);
        string? chosen = null;

        // The gate is held only while the project is made; choosing it starts the next flow, which takes the gate itself.
        await ExclusiveAsync(cancellationToken, async () =>
        {
            ProjectRow.MarkRunning();
            var made = await _setup.CreateProjectAsync(id, installation, cancellationToken);
            if (!made.IsOk)
            {
                ProjectRow.Apply(made, id);
                await ShowProjectListAsync(cancellationToken);
                return;
            }

            chosen = made.ProjectId ?? id;
        });

        if (chosen is not null)
        {
            await ChooseProjectCommand.ExecuteAsync(chosen);
        }
    }

    private async Task PerformProjectActionAsync(CancellationToken cancellationToken)
    {
        switch (ProjectRow.ActionKind)
        {
            case SetupActionKind.CopyText:
                ProjectNoticeText = _strings.GetString(_shell is not null && _shell.CopyText(ProjectRow.RequestText) ? "WizardCopied" : "WizardCopyFailed");
                break;
            case SetupActionKind.CreateProject:
                await CreateProjectCommand.ExecuteAsync(null);
                break;
            case SetupActionKind.PickProject:
                await PickAnotherProjectAsync(cancellationToken);
                break;
            case SetupActionKind.None:
                break;
            default:
                await ContinueFromProjectAsync(cancellationToken);
                break;
        }
    }

    /// <summary>Done: every step is green (or the GPU allowance was skipped), so go and make the first run.</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish() => _navigator.NavigateTo("NewRun");

    private bool CanFinish => !_isFlowBusy && ProjectRow.IsOk && SetupSteps is { IsAllDone: true };

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
