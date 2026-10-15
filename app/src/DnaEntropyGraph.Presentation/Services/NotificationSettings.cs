using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The three "when a run finishes" switches (issue #643), read where a run finishes (<see cref="RunCompletionActions"/>) and
/// edited in Settings: Notify when done (default On), Open the output folder when done (Off), Open results when done (Off: a page that jumps away mid-edit loses the user's place).
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
        get => Read(OpenResultsKey, defaultValue: false);
        set => Write(OpenResultsKey, value);
    }

    /// <summary>
    /// The three switches as they are now, read once. The settings store is a mutex with a 300 ms budget, so a caller on a
    /// run-driving thread reads this from the thread pool (<see cref="RunCompletionActions"/>) instead of the properties.
    /// </summary>
    public NotificationChoices Snapshot() => new(NotifyWhenDone, OpenFolderWhenDone, OpenResultsWhenDone);

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

/// <summary>One read of <see cref="NotificationSettings"/>: a stable answer for the whole completion path.</summary>
public readonly record struct NotificationChoices(bool NotifyWhenDone, bool OpenFolderWhenDone, bool OpenResultsWhenDone);
