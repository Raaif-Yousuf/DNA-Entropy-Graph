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

    public WizardViewModel(IGcpAccount gcpAccount, IDialogService dialogService, INavigator navigator, IStringResourceProvider strings, IDispatcher? dispatcher = null)
    {
        _strings = strings;
        _dispatcher = dispatcher;
        _gcpAccount = gcpAccount;
        _dialogService = dialogService;
        _navigator = navigator;
        _isSignedIn = gcpAccount.IsSignedIn;
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
    }}
