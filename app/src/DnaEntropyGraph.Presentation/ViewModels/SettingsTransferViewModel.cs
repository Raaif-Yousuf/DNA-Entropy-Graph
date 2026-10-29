using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.SettingsPortability;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One line of the import preview: the setting's plain name, what it is now, and what the file would make it.</summary>
public sealed record SettingsChangeRow(string Label, string Current, string Incoming);

/// <summary>
/// The Settings "Share settings" group (issue #242): Export settings writes the allowlisted settings and the user's presets to a
/// JSON file the user picks; Import settings reads one, shows what would change, and writes only on Apply. Nothing secret is in the
/// file (see <see cref="SettingsCatalog"/>), and the user's own files are only ever read (Hard Rule 14). After an apply, every
/// open Settings group is told to re-read the store (the same message Reset all settings sends).
/// </summary>
public sealed partial class SettingsTransferViewModel : ObservableObject
{
    private readonly SettingsTransfer _transfer;
    private readonly IFilePicker _picker;
    private readonly IStringResourceProvider _strings;
    private readonly IThemeApplier _theme;
    private readonly IMessenger _messenger;

    private SettingsImportPlan? _plan;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _previewSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelImportCommand))]
    private bool _hasPreview;

    public SettingsTransferViewModel(SettingsTransfer transfer, IFilePicker picker, IStringResourceProvider strings, IThemeApplier theme, IMessenger messenger)
    {
        _transfer = transfer;
        _picker = picker;
        _strings = strings;
        _theme = theme;
        _messenger = messenger;
    }

    /// <summary>The settings the chosen file would change. Empty until a usable file is chosen and again after Apply or Cancel.</summary>
    public ObservableCollection<SettingsChangeRow> Changes { get; } = [];

    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = await _picker.PickSaveSettingsAsync(_strings.GetString("SettingsTransferFileName"), cancellationToken);
            if (path is null)
            {
                return;
            }

            await File.WriteAllTextAsync(path, _transfer.Export(), new System.Text.UTF8Encoding(false), cancellationToken);
            Status = string.Format(CultureInfo.CurrentCulture, _strings.GetString("SettingsTransferExportDone"), path);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled: nothing to report.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsUnavailableException)
        {
            Status = _strings.GetString("SettingsTransferExportFailed");
        }
    }

    [RelayCommand]
    private async Task ChooseImportFileAsync(CancellationToken cancellationToken)
    {
        string text;
        try
        {
            var path = await _picker.PickSettingsFileAsync(cancellationToken);
            if (path is null)
            {
                return;
            }

            // Only read, never written back (Hard Rule 14). A settings file is a few hundred bytes; a huge one is not one of ours.
            if (new FileInfo(path).Length > 1_000_000)
            {
                ClearPreview();
                Status = _strings.GetString("SettingsTransferImportNotSettings");
                return;
            }

            text = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClearPreview();
            Status = _strings.GetString("SettingsTransferImportReadFailed");
            return;
        }

        SettingsImportPlan plan;
        try
        {
            plan = _transfer.Plan(text);
        }
        catch (SettingsUnavailableException)
        {
            ClearPreview();
            Status = _strings.GetString("SettingsTransferImportLocked");
            return;
        }

        ClearPreview();
        switch (plan.Status)
        {
            case SettingsImportStatus.Ok:
                _plan = plan;
                foreach (var change in plan.Changes)
                {
                    Changes.Add(new SettingsChangeRow(_strings.GetString(change.LabelKey), Show(change.Current), Show(change.Incoming)));
                }

                PreviewSummary = string.Format(CultureInfo.CurrentCulture, _strings.GetString("SettingsTransferPreviewSummary"), plan.Changes.Count, plan.PresetCount);
                HasPreview = true;
                Status = plan.IgnoredCount + plan.RejectedCount > 0
                    ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("SettingsTransferPreviewSkipped"), plan.IgnoredCount + plan.RejectedCount)
                    : string.Empty;
                break;
            case SettingsImportStatus.NewerVersion:
                Status = _strings.GetString("SettingsTransferImportNewer");
                break;
            case SettingsImportStatus.NothingToImport:
                Status = _strings.GetString("SettingsTransferImportNothing");
                break;
            default:
                Status = _strings.GetString("SettingsTransferImportNotSettings");
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasPreview))]
    private void ApplyImport()
    {
        if (_plan is null)
        {
            return;
        }

        try
        {
            _transfer.Apply(_plan);
        }
        catch (SettingsUnavailableException)
        {
            // Some settings may already be written (each is saved on its own); the preview stays so the user can apply again.
            Status = _strings.GetString("SettingsTransferImportLocked");
            return;
        }

        var applied = _plan;
        ClearPreview();
        if (applied.Changes.FirstOrDefault(c => c.Key == "Theme") is { } theme)
        {
            _theme.Apply(theme.Incoming);
        }

        _messenger.Send(new SettingsResetMessage());
        Status = string.Format(CultureInfo.CurrentCulture, _strings.GetString("SettingsTransferImportDone"), applied.Changes.Count, applied.PresetCount);
    }

    [RelayCommand(CanExecute = nameof(HasPreview))]
    private void CancelImport()
    {
        ClearPreview();
        Status = string.Empty;
    }

    private void ClearPreview()
    {
        _plan = null;
        Changes.Clear();
        PreviewSummary = string.Empty;
        HasPreview = false;
    }

    private string Show(string? value) => value switch
    {
        null => _strings.GetString("SettingsTransferValueDefault"),
        "true" => _strings.GetString("SettingsTransferValueOn"),
        "false" => _strings.GetString("SettingsTransferValueOff"),
        _ => value,
    };
}
