using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.PrototypeImport;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Bringing in the earlier command-line version's runs (issue #243). On the first launch the app looks once (off the UI thread,
/// read-only) and, if it finds runs it does not have, asks before adding them; the Settings button "Look for earlier runs" does
/// the same on demand. The zone and SSH key the old tool remembered are only shown in the question (the app picks zones itself and
/// uses no SSH key). Nothing in the user's folders is touched (Hard Rule 14).
/// </summary>
public sealed partial class PrototypeImportViewModel : ObservableObject
{
    /// <summary>The settings key set once the first-launch look has happened, whatever it found.</summary>
    public const string DoneKey = "prototype_import_done";

    private readonly ISettingsStore _settings;
    private readonly PrototypeSearchPaths _paths;
    private readonly PrototypeImporter _importer;
    private readonly IDialogService _dialogs;
    private readonly IStringResourceProvider _strings;
    private readonly IToastService _toasts;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    private bool _isBusy;

    public PrototypeImportViewModel(ISettingsStore settings, PrototypeSearchPaths paths, PrototypeImporter importer, IDialogService dialogs, IStringResourceProvider strings, IToastService toasts)
    {
        _settings = settings;
        _paths = paths;
        _importer = importer;
        _dialogs = dialogs;
        _strings = strings;
        _toasts = toasts;
    }

    /// <summary>Looks once, on the first launch of a profile. Never throws; a locked settings file means it asks again next launch.</summary>
    public async Task OfferOnFirstLaunchAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_settings.GetString(DoneKey) == "true")
            {
                return;
            }
        }
        catch (SettingsUnavailableException)
        {
            return;
        }

        await LookAndOfferAsync(markDone: true, askEvenIfNothingNew: false, cancellationToken);
    }

    /// <summary>The Settings button: look again now, whatever was answered at the first launch.</summary>
    [RelayCommand(CanExecute = nameof(CanFind))]
    private Task FindAsync(CancellationToken cancellationToken) => LookAndOfferAsync(markDone: false, askEvenIfNothingNew: true, cancellationToken);

    private bool CanFind() => !IsBusy;

    private async Task LookAndOfferAsync(bool markDone, bool askEvenIfNothingNew, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var scan = await Task.Run(() => PrototypeScanner.Scan(_paths.ConfigPath, _paths.Downloads), cancellationToken);
            if (markDone)
            {
                MarkDone();
            }

            var fresh = await _importer.NewRunsAsync(scan, cancellationToken);
            if (fresh.Count == 0)
            {
                // The zone and key alone are not worth a question: there is nothing to add.
                Status = askEvenIfNothingNew ? _strings.GetString(scan.Runs.Count == 0 ? "PrototypeImportNothingFound" : "PrototypeImportAlreadyDone") : string.Empty;
                return;
            }

            var yes = await _dialogs.ConfirmAsync(_strings.GetString("PrototypeImportTitle"), Describe(scan, fresh.Count), cancellationToken);
            if (!yes)
            {
                Status = string.Empty;
                return;
            }

            var added = await _importer.ImportAsync(scan, cancellationToken);
            Status = string.Format(CultureInfo.CurrentCulture, _strings.GetString("PrototypeImportDone"), added);
            _toasts.ShowToast(_strings.GetString("PrototypeImportTitle"), Status, ToastSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            // Closing the app while looking: nothing to report.
        }
        catch (Exception ex)
        {
            // Startup must never stop on an import problem: only the class is traced (a message can carry a path).
            System.Diagnostics.Trace.TraceWarning($"prototype import failed: {ex.GetType().Name}");
            Status = _strings.GetString("PrototypeImportFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void MarkDone()
    {
        try
        {
            _settings.SetString(DoneKey, "true");
        }
        catch (SettingsUnavailableException)
        {
            // It will look again next launch; harmless.
        }
    }

    private string Describe(PrototypeScan scan, int fresh)
    {
        var text = string.Format(CultureInfo.CurrentCulture, _strings.GetString("PrototypeImportBody"), fresh);
        if (scan.LastZone is not null)
        {
            text += "\n\n" + string.Format(CultureInfo.CurrentCulture, _strings.GetString("PrototypeImportZoneLine"), scan.LastZone);
        }

        if (scan.SshKeyFile is not null)
        {
            text += "\n\n" + string.Format(CultureInfo.CurrentCulture, _strings.GetString("PrototypeImportSshLine"), scan.SshKeyFile);
        }

        return text;
    }
}
