using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>Stage timeline + cost ticker for one active run (docs/superpowers/specs Appendix A section 2.4).</summary>
public sealed partial class RunProgressViewModel : ObservableObject
{
    private readonly IJobEngine _jobEngine;
    private readonly IDispatcher _dispatcher;

    [ObservableProperty]
    private string? _jobId;

    [ObservableProperty]
    private double _fractionComplete;

    public RunProgressViewModel(IJobEngine jobEngine, IDispatcher dispatcher)
    {
        _jobEngine = jobEngine;
        _dispatcher = dispatcher;
    }

    [RelayCommand]
    private async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (JobId is null)
        {
            return;
        }

        await _jobEngine.CancelRunAsync(JobId, cancellationToken).ConfigureAwait(false);
        _dispatcher.Enqueue(() => FractionComplete = 0);
    }
}
