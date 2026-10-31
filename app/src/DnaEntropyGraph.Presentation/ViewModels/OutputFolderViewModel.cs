using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The "Save results to" folder (issue #1037). ONE instance (singleton) is shown on Settings (Run defaults) and on New run, so the two can never
/// disagree. The choice is one settings key, <see cref="SettingKey"/>; blank or missing means the default (Downloads, resolved by the engines through
/// <c>RunOutputFolders.DefaultParent</c>), so <see cref="FolderPath"/> is null then. A folder goes through <see cref="OutputFolderChoice.Check"/>
/// before it is saved: a refused folder shows its message beside the control and nothing is written. The user's files are only ever read (Hard Rule 14):
/// this class stores a path, it creates and deletes nothing.
/// </summary>
public sealed partial class OutputFolderViewModel : ObservableObject
{
    /// <summary>The settings key. A path on this PC, so it is listed as excluded from Export settings in <c>SettingsCatalog.Excluded</c>.</summary>
    public const string SettingKey = "output_folder";

    private readonly ISettingsStore _store;
    private readonly IFilePicker _picker;
    private readonly IStringResourceProvider _strings;
    private readonly IDiagnosticsLog _log;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    public OutputFolderViewModel(ISettingsStore store, IFilePicker picker, IStringResourceProvider strings, IMessenger? messenger = null, IDiagnosticsLog? log = null)
    {
        _log = log ?? NullDiagnosticsLog.Instance;
        _store = store;
        _picker = picker;
        _strings = strings;
        FolderPath = Read();
        messenger?.Register<OutputFolderViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
    }

    /// <summary>The chosen folder, or null for the default. Not bound itself (the page shows <see cref="DisplayText"/>); the next run reads it.</summary>
    public string? FolderPath { get; private set; }

    /// <summary>What the page shows: the chosen folder, or the plain words for the default.</summary>
    public string DisplayText => FolderPath ?? _strings.GetString("OutputFolder_Default_Text");

    public bool HasMessage => Message.Length > 0;

    [RelayCommand]
    private async Task ChangeAsync(CancellationToken cancellationToken)
    {
        string? picked;
        try
        {
            picked = await _picker.PickOutputFolderAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Message = string.Empty;
            return;
        }
        catch (Exception ex)
        {
            // A picker that throws (a bad window handle, a COM failure) must not make the button silently do nothing. The class only, never the path.
            _log.Warning("output_folder_picker", null, ex.GetType().Name);
            Message = _strings.GetString("OutputFolder_PickFailed_Message");
            return;
        }

        Message = string.Empty;
        if (string.IsNullOrWhiteSpace(picked))
        {
            // Cancelled, or a folder with no local path: nothing changes.
            return;
        }

        var verdict = OutputFolderChoice.Check(picked, _strings);
        if (!verdict.Accepted)
        {
            Message = verdict.Message ?? string.Empty;
            return;
        }

        Save(picked);
    }

    [RelayCommand]
    private void UseDownloads()
    {
        Message = string.Empty;
        Save(string.Empty);
    }

    private void Save(string value)
    {
        try
        {
            _store.SetString(SettingKey, value);
        }
        catch (SettingsUnavailableException)
        {
            Message = _strings.GetString("OutputFolder_SaveFailed_Message");
            return;
        }

        SetFolder(value.Length == 0 ? null : value);
    }

    private void Reload()
    {
        SetFolder(Read());
        Message = string.Empty;
    }

    private void SetFolder(string? value)
    {
        FolderPath = value;
        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(DisplayText));
    }

    private string? Read() => OutputFolderSetting.Read(_store);
}
