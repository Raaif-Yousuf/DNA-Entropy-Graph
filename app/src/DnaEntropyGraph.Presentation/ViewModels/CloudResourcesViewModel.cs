using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>Every app=dna-entropy-graph labelled VM/disk/bucket with cost (docs/superpowers/specs Appendix A section 2.7).</summary>
public sealed partial class CloudResourcesViewModel : ObservableObject
{
    private readonly IGcpAccount _gcpAccount;
    private readonly IDialogService _dialogService;
    private readonly IStringResourceProvider _strings;

    [ObservableProperty]
    private string? _selectedProjectId;

    public CloudResourcesViewModel(IGcpAccount gcpAccount, IDialogService dialogService, IStringResourceProvider strings)
    {
        _gcpAccount = gcpAccount;
        _dialogService = dialogService;
        _strings = strings;
        _selectedProjectId = gcpAccount.SelectedProjectId;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync(CancellationToken cancellationToken)
    {
        // Plain (non-dotted) resw keys: this call site never goes through
        // x:Uid, and a dotted key here would miss the PRI's compiled
        // resource path (see ShellViewModel.BuildStatusPillText's comment).
        await _dialogService.ConfirmAsync(
            _strings.GetString("ConfirmDeleteResource_Title"),
            _strings.GetString("ConfirmDeleteResource_Body"),
            cancellationToken).ConfigureAwait(false);
    }
}
