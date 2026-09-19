using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>Drop a file, choose options, press Run (docs/superpowers/specs Appendix A section 2.2).</summary>
public sealed partial class NewRunViewModel : ObservableObject
{
    private readonly IFilePicker _filePicker;
    private readonly ISettingsStore _settingsStore;
    private readonly IJobEngine _jobEngine;
    private readonly INavigator _navigator;

    [ObservableProperty]
    private string? _selectedInputPath;

    [ObservableProperty]
    private string _modelId = "evo2_7b";

    public NewRunViewModel(IFilePicker filePicker, ISettingsStore settingsStore, IJobEngine jobEngine, INavigator navigator)
    {
        _filePicker = filePicker;
        _settingsStore = settingsStore;
        _jobEngine = jobEngine;
        _navigator = navigator;
    }

    [RelayCommand]
    private async Task BrowseAsync(CancellationToken cancellationToken)
    {
        SelectedInputPath = await _filePicker.PickInputFileAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private async Task StartRunAsync(CancellationToken cancellationToken)
    {
        _settingsStore.SetString("LastModelId", ModelId);
        var options = new RunOptions { ModelId = ModelId, RunTarget = "Cloud" };
        var jobId = await _jobEngine.StartRunAsync(options, cancellationToken).ConfigureAwait(false);
        _navigator.NavigateTo("RunProgress", jobId);
    }

    private bool CanStartRun() => !string.IsNullOrWhiteSpace(SelectedInputPath);
}
