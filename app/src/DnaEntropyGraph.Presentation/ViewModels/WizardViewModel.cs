using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The first-run wizard's eight steps (Welcome, Sign in, Project, Billing,
/// APIs, Storage, Quota, Test run, Done) - docs/superpowers/specs Appendix A
/// section 2.1. The skeleton models sign-in only; the remaining steps are a
/// follow-up issue.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly IGcpAccount _gcpAccount;
    private readonly IDialogService _dialogService;
    private readonly INavigator _navigator;
    private readonly IStringResourceProvider _strings;
    private readonly IDispatcher? _dispatcher;

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

    public WizardViewModel(IGcpAccount gcpAccount, IDialogService dialogService, INavigator navigator, IStringResourceProvider strings, IDispatcher? dispatcher = null)
    {
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
                _navigator.NavigateTo("Wizard/Project");
            }
        });
    }

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
