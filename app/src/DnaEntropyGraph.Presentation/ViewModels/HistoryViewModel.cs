using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>Every past run, newest first (docs/superpowers/specs Appendix A section 2.6).</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IRunRepository _runRepository;
    private readonly INavigator _navigator;

    public ObservableCollection<RunRecord> Runs { get; } = new();

    public HistoryViewModel(IRunRepository runRepository, INavigator navigator)
    {
        _runRepository = runRepository;
        _navigator = navigator;
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var runs = await _runRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        Runs.Clear();
        foreach (var run in runs.OrderByDescending(r => r.CreatedUtc))
        {
            Runs.Add(run);
        }
    }

    [RelayCommand]
    private void OpenRun(string jobId) => _navigator.NavigateTo("Results", jobId);
}
