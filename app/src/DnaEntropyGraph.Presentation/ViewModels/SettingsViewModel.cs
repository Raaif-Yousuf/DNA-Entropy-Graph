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
    private const int SystemThemeIndex = 2;

    // The saved strings, in the order the Appearance radio buttons list them.
    private static readonly string[] ThemeNames = ["Light", "Dark", "System"];

    private readonly ISettingsStore _settingsStore;
    private readonly IToastService _toastService;
    private readonly IStringResourceProvider _strings;
    private readonly IDiagnosticsExporter _diagnostics;
    private readonly IFilePicker _filePicker;
    private readonly IFolderLauncher _folderLauncher;
    private readonly TimeProvider _time;
    private readonly IThemeApplier _themeApplier;

    /// <summary>
    /// The chosen theme as the position of the Appearance RadioButtons item (0 Light, 1 Dark, 2 Use system). The control's
    /// selection is the single source: a click, an arrow key or a touch all arrive as this property changing, which applies
    /// the theme at once and saves it (#639). Anything saved that is not "Light" or "Dark" reads as Use system.
    /// </summary>
    [ObservableProperty]
    private int _themeIndex;
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
        TimeProvider time,
        IThemeApplier themeApplier)
    {
        _settingsStore = settingsStore;
        _toastService = toastService;
        _strings = strings;
        _diagnostics = diagnostics;
        _filePicker = filePicker;
        _folderLauncher = folderLauncher;
        _time = time;
        _themeApplier = themeApplier;
        // The field, not the property: opening the page must not re-apply or re-save what was just read.
        _themeIndex = Array.IndexOf(ThemeNames, ReadTheme(settingsStore)) is var i and >= 0 ? i : SystemThemeIndex;
    }

    private static string? ReadTheme(ISettingsStore settingsStore)
    {
        try
        {
            return settingsStore.GetString(ThemeKey);
        }
        catch (SettingsUnavailableException ex)
        {
            // #558: a locked settings file must not crash opening Settings.
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while reading theme: {ex.GetType().Name}");
            return null;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (value < 0 || value >= ThemeNames.Length)
        {
            // A RadioButtons control reports -1 when nothing is selected; there is no theme to apply.
            return;
        }

        var theme = ThemeNames[value];
        // Applied before the save: a locked settings file (#558) must not stop the window changing for this session.
        _themeApplier.Apply(theme);
        try
        {
            _settingsStore.SetString(ThemeKey, theme);
        }
        catch (SettingsUnavailableException ex)
        {
            // The theme still applies for this session; it just is not remembered (#558), so say so.
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving theme: {ex.GetType().Name}");
            _toastService.ShowToast(_strings.GetString("ThemeNotSaved_Title"), _strings.GetString("ThemeNotSaved_Body"));
            return;
        }

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
