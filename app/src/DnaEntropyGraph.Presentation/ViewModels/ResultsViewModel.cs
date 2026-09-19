using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>The entropy overview chart + igv.js viewer + export actions (docs/superpowers/specs Appendix A section 2.5).</summary>
public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly IRunRepository _runRepository;
    private readonly IToastService _toastService;

    [ObservableProperty]
    private string? _jobId;

    public ResultsViewModel(IRunRepository runRepository, IToastService toastService)
    {
        _runRepository = runRepository;
        _toastService = toastService;
    }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var runs = await _runRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (runs.Count == 0)
        {
            _toastService.ShowToast("No runs yet", "Start a run to see results here.");
        }
    }
}
