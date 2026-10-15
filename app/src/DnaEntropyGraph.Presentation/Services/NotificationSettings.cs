using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The three "when a run finishes" switches (issue #643), read where a run finishes (<see cref="RunCompletionActions"/>) and
/// edited in Settings: Notify when done (default On), Open the output folder when done (Off), Open results when done (On).
/// One class, so the page and the completion path cannot disagree about a key or a default. A read of a locked settings file
/// is the default; a write of one throws <see cref="SettingsUnavailableException"/> for the caller to report.
/// </summary>
public sealed class NotificationSettings(ISettingsStore store)
{
    public const string NotifyKey = "notify_when_done";

    public const string OpenFolderKey = "open_folder_when_done";

    public const string OpenResultsKey = "open_results_when_done";

    public bool NotifyWhenDone
    {
        get => Read(NotifyKey, defaultValue: true);
        set => Write(NotifyKey, value);
    }

    public bool OpenFolderWhenDone
    {
        get => Read(OpenFolderKey, defaultValue: false);
        set => Write(OpenFolderKey, value);
    }

    public bool OpenResultsWhenDone
    {
        get => Read(OpenResultsKey, defaultValue: true);
        set => Write(OpenResultsKey, value);
    }

    private bool Read(string key, bool defaultValue)
    {
        try
        {
            return bool.TryParse(store.GetString(key), out var value) ? value : defaultValue;
        }
        catch (SettingsUnavailableException)
        {
            return defaultValue;
        }
    }

    private void Write(string key, bool value) => store.SetString(key, value ? "true" : "false");
}
