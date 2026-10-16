using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Presets;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One row of the preset picker: the preset and the words to show for it.</summary>
public sealed record PresetItem(RunPreset Preset, string DisplayName, string Detail);

/// <summary>
/// The "Option preset" picker on the New run page (issue #136). Choosing a preset sets how the strands combine, the context,
/// the shape (line or circle) and the files that come back; any edit of the file boxes afterwards drops the preset, because the
/// options are then the user's own. The summary line always lists what the run will use, and the preset's id and name are
/// recorded with the run. Sharing is JSON through the clipboard (<see cref="PresetStore.ExportJson"/> / <see cref="PresetStore.Import"/>).
/// </summary>
public sealed partial class PresetPickerViewModel : ObservableObject
{
    private readonly PresetStore _store;
    private readonly IStringResourceProvider _strings;
    private readonly IShellLauncher _launcher;
    private readonly IClipboardTextReader _clipboard;
    private OutputFileKinds? _outputs;
    private bool _selecting;

    [ObservableProperty]
    private PresetItem? _selectedItem;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public PresetPickerViewModel(PresetStore store, IStringResourceProvider strings, IShellLauncher launcher, IClipboardTextReader clipboard)
    {
        _store = store;
        _strings = strings;
        _launcher = launcher;
        _clipboard = clipboard;
        Reload(null);
        UpdateSummary(null);
    }

    /// <summary>Built-ins first, then the user's own.</summary>
    public ObservableCollection<PresetItem> Items { get; } = [];

    /// <summary>How the strands combine for the next run.</summary>
    public Direction Direction { get; private set; } = Direction.BothCombined;

    /// <summary>The context length K for the next run.</summary>
    public int ContextLength { get; private set; } = new RunOptions { ModelId = string.Empty, RunTarget = string.Empty }.ContextLength;

    /// <summary>Whether the next run reads each sequence as a line or a circle.</summary>
    public Topology Topology { get; private set; } = Topology.Auto;

    /// <summary>The chosen preset's id, or null when the options are the user's own.</summary>
    public string? PresetId => SelectedItem?.Preset.Id;

    /// <summary>The chosen preset's name as shown, or null.</summary>
    public string? PresetName => SelectedItem?.DisplayName;

    /// <summary>True when the chosen preset is one of the user's own (so Delete applies).</summary>
    public bool CanDelete => SelectedItem is { Preset.BuiltIn: false };

    /// <summary>True while a preset's one-line description should show.</summary>
    public bool HasDetail => SelectedItem is not null;

    public string DetailText => SelectedItem?.Detail ?? string.Empty;

    /// <summary>Raised when choosing a preset sets the file boxes; the page's view model puts the files in its boxes.</summary>
    public event Action<OutputFileKinds>? OutputsChosen;

    /// <summary>Called by the page's view model with the files currently ticked (null while no file is chosen yet), so Save and the summary use them.</summary>
    public void UpdateSummary(OutputFileKinds? outputs)
    {
        _outputs = outputs;
        var line = _strings.GetString("PresetSummary_Line");
        var files = outputs is { } o && o != OutputFileKinds.None
            ? string.Join(", ", OutputCatalog.Entries.Where(e => o.HasFlag(e.Kind)).Select(e => _strings.GetString(e.LabelKey)))
            : _strings.GetString("PresetSummary_NoFiles");
        SummaryText = string.Format(CultureInfo.CurrentCulture, line, _strings.GetString(DirectionKey(Direction)), _strings.GetString(TopologyKey(Topology)), ContextLength, files);
    }

    // Literal keys (not "PresetDirection_" + name) so the wiring guard can see each string is read.
    private static string DirectionKey(Direction direction) => direction switch
    {
        Direction.BothCombined => "PresetDirection_BothCombined",
        Direction.BothAveraged => "PresetDirection_BothAveraged",
        Direction.BothSeparate => "PresetDirection_BothSeparate",
        Direction.ForwardOnly => "PresetDirection_ForwardOnly",
        Direction.ReverseOnly => "PresetDirection_ReverseOnly",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    private static string TopologyKey(Topology topology) => topology switch
    {
        Topology.Auto => "PresetTopology_Auto",
        Topology.Linear => "PresetTopology_Linear",
        Topology.Circular => "PresetTopology_Circular",
        _ => throw new ArgumentOutOfRangeException(nameof(topology)),
    };
    /// <summary>The user changed the file boxes by hand: the options are no longer the preset's, so the preset is dropped (the other options stay as they were).</summary>
    public void NoteOutputsEdited()
    {
        if (_selecting || SelectedItem is null)
        {
            return;
        }

        _selecting = true;
        try
        {
            SelectedItem = null;
        }
        finally
        {
            _selecting = false;
        }
    }

    partial void OnSelectedItemChanged(PresetItem? value)
    {
        OnPropertyChanged(nameof(PresetId));
        OnPropertyChanged(nameof(PresetName));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailText));
        DeleteCommand.NotifyCanExecuteChanged();
        if (_selecting || value is null)
        {
            return;
        }

        _selecting = true;
        try
        {
            Direction = value.Preset.Direction;
            ContextLength = value.Preset.ContextLength;
            Topology = value.Preset.Topology;
            StatusText = string.Empty;
            OutputsChosen?.Invoke(value.Preset.OutputFiles);
        }
        finally
        {
            _selecting = false;
        }
    }

    [RelayCommand]
    private void SaveCurrent()
    {
        if (_outputs is not { } outputs || outputs == OutputFileKinds.None)
        {
            StatusText = _strings.GetString("PresetStatus_NeedFile");
            return;
        }

        var outcome = _store.Save(NewName, Direction, ContextLength, Topology, outputs);
        switch (outcome.Result)
        {
            case PresetSaveResult.EmptyName:
                StatusText = _strings.GetString("PresetStatus_EmptyName");
                return;
            case PresetSaveResult.NameTaken:
                StatusText = _strings.GetString("PresetStatus_NameTaken");
                return;
        }

        Reload(outcome.Preset!.Id);
        NewName = string.Empty;
        StatusText = string.Format(CultureInfo.CurrentCulture, _strings.GetString("PresetStatus_Saved"), outcome.Preset.Name);
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedItem is not { Preset.BuiltIn: false } item || !_store.Delete(item.Preset.Id))
        {
            return;
        }

        Reload(null);
        StatusText = string.Format(CultureInfo.CurrentCulture, _strings.GetString("PresetStatus_Deleted"), item.DisplayName);
    }

    [RelayCommand]
    private void CopyMine()
    {
        if (!_store.List().Any(p => !p.BuiltIn))
        {
            StatusText = _strings.GetString("PresetStatus_NothingToCopy");
            return;
        }

        StatusText = _strings.GetString(_launcher.CopyText(_store.ExportJson()) ? "PresetStatus_Copied" : "PresetStatus_CopyFailed");
    }

    [RelayCommand]
    private async Task AddFromClipboardAsync()
    {
        var text = await _clipboard.ReadTextAsync();
        var result = _store.Import(text);
        if (result.Problem)
        {
            StatusText = _strings.GetString("PresetStatus_ImportNone");
            return;
        }

        Reload(SelectedItem?.Preset.Id);
        StatusText = string.Format(CultureInfo.CurrentCulture, _strings.GetString("PresetStatus_Imported"), result.Imported);
    }

    private void Reload(string? select)
    {
        _selecting = true;
        try
        {
            Items.Clear();
            foreach (var preset in _store.List())
            {
                Items.Add(preset.BuiltIn
                    ? new PresetItem(preset, _strings.GetString(BuiltInPresets.NameKey(preset.Id)), _strings.GetString(BuiltInPresets.DetailKey(preset.Id)))
                    : new PresetItem(preset, preset.Name, string.Empty));
            }

            SelectedItem = select is null ? null : Items.FirstOrDefault(i => i.Preset.Id == select);
        }
        finally
        {
            _selecting = false;
        }
    }
}
