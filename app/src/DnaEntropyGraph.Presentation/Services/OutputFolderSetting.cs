using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Reads the saved "Save results to" folder (<see cref="OutputFolderViewModel.SettingKey"/>, issue #1037) at the moment it is asked, so a change on Settings
/// takes effect without a restart (issue #1006). The one reader for the setting: the view model and the export root both go through it.
/// </summary>
public static class OutputFolderSetting
{
    /// <summary>The saved folder, or null when none is saved or the settings file cannot be read.</summary>
    public static string? Read(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            var value = store.GetString(OutputFolderViewModel.SettingKey);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while reading the output folder: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The saved folder only when it is one a run may use: not a drive root (<see cref="OutputFolderChoice"/>; a drive root saved by hand in settings.json
    /// is not honoured) and a path that resolves. Otherwise null, meaning the default parent.
    /// </summary>
    public static string? ReadUsable(ISettingsStore store)
    {
        var path = Read(store);
        if (path is null)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return OutputFolderChoice.IsRefused(full) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
