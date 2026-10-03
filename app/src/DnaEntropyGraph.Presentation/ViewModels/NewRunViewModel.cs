using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Drop files, folders or pasted text, see each one checked locally, name the run, press Run
/// (issue #63, docs/superpowers/specs Appendix A section 2.2). Every file is validated here with the same
/// rules as the worker before any cloud resource exists (Hard Rule 2); <c>JobEngine</c> validates once more
/// at run start.
/// </summary>
public sealed partial class NewRunViewModel : ObservableObject
{
    private const string NameTemplateSettingKey = "NameTemplate";
    private const string DefaultNameTemplate = "{file}";

    private readonly IFilePicker _filePicker;
    private readonly ISettingsStore _settingsStore;
    private readonly IJobEngine _jobEngine;
    private readonly INavigator _navigator;
    private readonly IStringResourceProvider _strings;
    private readonly IPastedInputStore _pastedStore;
    private readonly TimeProvider _time;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunNamePreview))]
    [NotifyCanExecuteChangedFor(nameof(StartRunCommand))]
    private InputPillItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunNamePreview))]
    private string _modelId = "evo2_7b";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunNamePreview))]
    private string _nameTemplate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasteCountText))]
    [NotifyCanExecuteChangedFor(nameof(AddPastedCommand))]
    private string _pasteText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfferTreatAsRna))]
    private bool _isTreatingAsRna;

    /// <summary>A line of help for the user (a path that does not exist, a folder with nothing to run); empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public NewRunViewModel(
        IFilePicker filePicker,
        ISettingsStore settingsStore,
        IJobEngine jobEngine,
        INavigator navigator,
        IStringResourceProvider strings,
        IPastedInputStore pastedStore,
        TimeProvider time)
    {
        _filePicker = filePicker;
        _settingsStore = settingsStore;
        _jobEngine = jobEngine;
        _navigator = navigator;
        _strings = strings;
        _pastedStore = pastedStore;
        _time = time;
        var saved = settingsStore.GetString(NameTemplateSettingKey);
        _nameTemplate = string.IsNullOrWhiteSpace(saved) ? DefaultNameTemplate : saved;
    }

    /// <summary>One pill per file or pasted sequence, in the order added.</summary>
    public ObservableCollection<InputPillItem> Items { get; } = [];

    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>The live "N bases" counter under the Paste sequence box.</summary>
    public string PasteCountText => string.Format(
        CultureInfo.CurrentCulture,
        _strings.GetString("NewRunPasteCount"),
        PastedSequenceCounter.Count(PasteText));

    /// <summary>True while some file has a U and the run is not set to treat input as RNA: the page offers the "Treat as RNA" button.</summary>
    public bool OfferTreatAsRna => !IsTreatingAsRna && Items.Any(item => item.NeedsRnaChoice);

    /// <summary>What the run will be named, from <see cref="NameTemplate"/> and the selected input; empty when nothing is selected.</summary>
    public string RunNamePreview => SelectedItem is null
        ? string.Empty
        : RunNamer.Resolve(NameTemplate, SelectedItem.IsPasted ? null : SelectedItem.Path, ModelId, _time.GetLocalNow().DateTime, 1, []);

    /// <summary>Adds dropped or picked paths: a file becomes a pill, a folder contributes the sequence files directly inside it.</summary>
    [RelayCommand]
    private async Task AddPathsAsync(IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        StatusMessage = string.Empty;
        foreach (var path in paths ?? [])
        {
            if (Directory.Exists(path))
            {
                var files = InputFolderScanner.SequenceFiles(path);
                if (files.Count == 0)
                {
                    StatusMessage = Format("NewRunStatusFolderEmpty", path);
                }

                foreach (var file in files)
                {
                    await AddFileAsync(file, isPasted: false, cancellationToken);
                }
            }
            else
            {
                await AddFileAsync(path, isPasted: false, cancellationToken);
            }
        }
    }

    [RelayCommand]
    private async Task BrowseAsync(CancellationToken cancellationToken)
    {
        var picked = await _filePicker.PickInputFilesAsync(cancellationToken);
        await AddPathsAsync(picked, cancellationToken);
    }

    /// <summary>Adds what is in the Paste sequence box: the path of an existing file adds that file, anything else is a sequence saved under app data.</summary>
    [RelayCommand(CanExecute = nameof(CanAddPasted))]
    private async Task AddPastedAsync(CancellationToken cancellationToken)
    {
        StatusMessage = string.Empty;
        var resolution = InputResolver.Resolve(PasteText);
        switch (resolution.Kind)
        {
            case InputResolutionKind.ExistingFile:
                await AddFileAsync(resolution.FilePath!, isPasted: false, cancellationToken);
                break;
            case InputResolutionKind.PathNotFound:
                StatusMessage = Format("NewRunStatusPathNotFound", resolution.AttemptedPath!);
                return;
            case InputResolutionKind.PastedSequence:
                await AddFileAsync(_pastedStore.Save(PasteText), isPasted: true, cancellationToken);
                break;
            default:
                return;
        }

        PasteText = string.Empty;
    }

    private bool CanAddPasted() => !string.IsNullOrWhiteSpace(PasteText);

    [RelayCommand]
    private void RemoveItem(InputPillItem? item)
    {
        if (item is null)
        {
            return;
        }

        var index = Items.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        Items.RemoveAt(index);
        if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        }

        OnPropertyChanged(nameof(OfferTreatAsRna));
    }

    /// <summary>The "Treat as RNA" button: converts U to T for this run and checks every file again.</summary>
    [RelayCommand]
    private Task TreatAsRnaAsync(CancellationToken cancellationToken) => SetTreatAsRnaAsync(true, cancellationToken);

    [RelayCommand]
    private Task StopTreatingAsRnaAsync(CancellationToken cancellationToken) => SetTreatAsRnaAsync(false, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private async Task StartRunAsync(CancellationToken cancellationToken)
    {
        _settingsStore.SetString("LastModelId", ModelId);
        _settingsStore.SetString(NameTemplateSettingKey, NameTemplate);
        var options = new RunOptions
        {
            ModelId = ModelId,
            RunTarget = "Cloud",
            InputPath = SelectedItem!.Path,
            TreatAsRna = IsTreatingAsRna,
            NameTemplate = NameTemplate,
        };
        var jobId = await _jobEngine.StartRunAsync(options, cancellationToken);
        _navigator.NavigateTo("RunProgress", jobId);
    }

    private bool CanStartRun() => SelectedItem is { IsValid: true };

    private async Task SetTreatAsRnaAsync(bool value, CancellationToken cancellationToken)
    {
        IsTreatingAsRna = value;
        foreach (var item in Items.ToList())
        {
            await ValidateAsync(item, cancellationToken);
        }
    }

    private async Task AddFileAsync(string path, bool isPasted, CancellationToken cancellationToken)
    {
        if (Items.Any(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var pill = new InputPillItem(path, isPasted, _strings);
        Items.Add(pill);
        SelectedItem ??= pill;
        await ValidateAsync(pill, cancellationToken);
    }

    private async Task ValidateAsync(InputPillItem pill, CancellationToken cancellationToken)
    {
        pill.BeginChecking();
        var treatAsRna = IsTreatingAsRna;
        var result = await Task.Run(
            () => InputFileValidator.Validate(pill.Path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna),
            cancellationToken);
        pill.Apply(result);
        OnPropertyChanged(nameof(OfferTreatAsRna));
        StartRunCommand.NotifyCanExecuteChanged();
    }

    private string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);
}
