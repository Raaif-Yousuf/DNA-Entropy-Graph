using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>Every app=dna-entropy-graph labelled VM/disk/bucket with cost (docs/superpowers/specs Appendix A section 2.7).</summary>
public sealed partial class CloudResourcesViewModel : ObservableObject
{
    private readonly IGcpAccount _gcpAccount;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private string? _selectedProjectId;

    public CloudResourcesViewModel(IGcpAccount gcpAccount, IDialogService dialogService)
    {
        _gcpAccount = gcpAccount;
        _dialogService = dialogService;
        _selectedProjectId = gcpAccount.SelectedProjectId;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync(CancellationToken cancellationToken)
    {
        await _dialogService.ConfirmAsync(
            "Delete this cloud resource?",
            "This stops billing for it. This cannot be undone.",
            cancellationToken).ConfigureAwait(false);
    }
}
