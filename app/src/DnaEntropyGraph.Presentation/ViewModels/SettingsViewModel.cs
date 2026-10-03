using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// SettingsCards: theme, default output folder, accounts, diagnostics zip (docs/superpowers/specs Appendix A section 2.8).
/// Issue #106 built the Diagnostics group; #104 adds the rest of the page around it.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>The wording the run-error strings use to send a user here ("Choose Save diagnostics in Settings"). A guard pins the button label to it.</summary>
    public const string SaveDiagnosticsLabel = "Save diagnostics";

    private const string ThemeKey = "Theme";

    private readonly ISettingsStore _settingsStore;
    private readonly IToastService _toastService;
    private readonly IStringResourceProvider _strings;
    private readonly IDiagnosticsExporter _diagnostics;
    private readonly IFilePicker _filePicker;
    private readonly IFolderLauncher _folderLauncher;
    private readonly TimeProvider _time;

    [ObservableProperty]
    private string _theme;

    /// <summary>The plain-words result of the last Save diagnostics, shown under the button. Empty before the first one.</summary>
    [ObservableProperty]
    private string _diagnosticsStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDiagnosticsCommand))]
    private bool _isSavingDiagnostics;

    // Not an observable property: only OpenDiagnosticsFolderCommand's CanExecute depends on it.
    private string? _lastDiagnosticsPath;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        IToastService toastService,
        IStringResourceProvider strings,
        IDiagnosticsExporter diagnostics,
        IFilePicker filePicker,
        IFolderLauncher folderLauncher,
        TimeProvider time)
    {
        _settingsStore = settingsStore;
        _toastService = toastService;
        _strings = strings;
        _diagnostics = diagnostics;
        _filePicker = filePicker;
        _folderLauncher = folderLauncher;
        _time = time;
        _theme = settingsStore.GetString(ThemeKey) ?? "System";
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        Theme = theme;
        _settingsStore.SetString(ThemeKey, theme);
        // Plain (non-dotted) resw key: see ShellViewModel.BuildStatusPillText's comment.
        _toastService.ShowToast(_strings.GetString("ThemeUpdated_Title"), theme);
    }

    /// <summary>Asks where to save, builds the zip off the UI thread, and says what happened. Never throws to the caller.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveDiagnostics))]
    private async Task SaveDiagnosticsAsync(CancellationToken cancellationToken)
    {
        IsSavingDiagnostics = true;
        try
        {
            var suggested = $"{_strings.GetString("DiagnosticsFileNamePrefix")}-{_time.GetLocalNow():yyyy-MM-dd}.zip";
            var path = await _filePicker.PickSaveZipAsync(suggested, cancellationToken);
            if (path is null)
            {
                return;
            }

            // The build reads every log and run file: never on the UI thread. Task.Run gets no token on purpose: a Task.Run
            // bound to an already-cancelled token never starts the delegate, and the exporter's cleanup of the picker's empty
            // file would be skipped. The exporter observes the token itself and throws OperationCanceledException.
            await Task.Run(() => _diagnostics.ExportAsync(path, cancellationToken), CancellationToken.None);
            _lastDiagnosticsPath = path;
            OpenDiagnosticsFolderCommand.NotifyCanExecuteChanged();
            Report("DiagnosticsSaved_Title", string.Format(_strings.GetString("DiagnosticsSaved_Body"), path));
        }
        catch (OperationCanceledException)
        {
            // The user cancelled: nothing to report.
        }
        catch (DiagnosticsLeakException)
        {
            Report("DiagnosticsSaveFailed_Title", _strings.GetString("DiagnosticsSaveFailed_Refused"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("DiagnosticsSaveFailed_Title", _strings.GetString("DiagnosticsSaveFailed_Write"));
        }
        catch (Exception ex)
        {
            // The picker, the history database or anything else: a command must never throw into the UI. Only the class
            // is traced (a message can carry a path or a name).
            System.Diagnostics.Trace.TraceError($"Save diagnostics failed: {ex.GetType().Name}");
            Report("DiagnosticsSaveFailed_Title", _strings.GetString("DiagnosticsSaveFailed_Other"));
        }
        finally
        {
            IsSavingDiagnostics = false;
        }
    }

    private bool CanSaveDiagnostics() => !IsSavingDiagnostics;

    [RelayCommand(CanExecute = nameof(HasSavedDiagnostics))]
    private void OpenDiagnosticsFolder()
    {
        var opened = false;
        try
        {
            opened = _folderLauncher.RevealFile(_lastDiagnosticsPath!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Open diagnostics folder failed: {ex.GetType().Name}");
        }

        if (!opened)
        {
            Report("DiagnosticsOpenFailed_Title", string.Format(_strings.GetString("DiagnosticsOpenFailed_Body"), _lastDiagnosticsPath));
        }
    }

    private bool HasSavedDiagnostics() => _lastDiagnosticsPath is not null;

    private void Report(string titleKey, string body)
    {
        DiagnosticsStatus = body;
        _toastService.ShowToast(_strings.GetString(titleKey), body);
    }
}
