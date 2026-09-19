using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>The entropy overview chart + igv.js viewer + export actions (docs/superpowers/specs Appendix A section 2.5).</summary>
public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly IRunRepository _runRepository;
    private readonly IToastService _toastService;
    private readonly IStringResourceProvider _strings;

    [ObservableProperty]
    private string? _jobId;

    public ResultsViewModel(IRunRepository runRepository, IToastService toastService, IStringResourceProvider strings)
    {
        _runRepository = runRepository;
        _toastService = toastService;
        _strings = strings;
    }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var runs = await _runRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (runs.Count == 0)
        {
            _toastService.ShowToast(_strings.GetString("NoRunsYet.Title"), _strings.GetString("NoRunsYet.Body"));
        }
    }
}
