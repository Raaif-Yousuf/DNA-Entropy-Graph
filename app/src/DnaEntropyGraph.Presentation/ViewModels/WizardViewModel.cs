using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;

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

    [ObservableProperty]
    private bool _isSignedIn;

    public WizardViewModel(IGcpAccount gcpAccount, IDialogService dialogService, INavigator navigator)
    {
        _gcpAccount = gcpAccount;
        _dialogService = dialogService;
        _navigator = navigator;
        _isSignedIn = gcpAccount.IsSignedIn;
    }

    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        await _gcpAccount.SignInAsync(cancellationToken).ConfigureAwait(false);
        IsSignedIn = _gcpAccount.IsSignedIn;
        if (IsSignedIn)
        {
            _navigator.NavigateTo("Wizard/Project");
        }
    }
}
