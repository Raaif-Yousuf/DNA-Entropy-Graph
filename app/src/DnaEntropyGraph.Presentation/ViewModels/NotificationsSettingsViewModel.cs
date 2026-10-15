using CommunityToolkit.Mvvm.ComponentModel;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>The Settings "Notifications" group (issue #643): three switches, each saved the moment it changes through <see cref="NotificationSettings"/>, the object the completion path reads.</summary>
public sealed partial class NotificationsSettingsViewModel : ObservableObject
{
    private readonly NotificationSettings _settings;
    private readonly IStringResourceProvider _strings;

    [ObservableProperty]
    private bool _notifyWhenDone;

    [ObservableProperty]
    private bool _openFolderWhenDone;

    [ObservableProperty]
    private bool _openResultsWhenDone;

    [ObservableProperty]
    private string _notificationsStatus = string.Empty;

    public NotificationsSettingsViewModel(NotificationSettings settings, IStringResourceProvider strings)
    {
        _settings = settings;
        _strings = strings;

        // Fields, not properties: opening the page must not save what was just read.
        _notifyWhenDone = settings.NotifyWhenDone;
        _openFolderWhenDone = settings.OpenFolderWhenDone;
        _openResultsWhenDone = settings.OpenResultsWhenDone;
    }

    partial void OnNotifyWhenDoneChanged(bool value) => Save(() => _settings.NotifyWhenDone = value);

    partial void OnOpenFolderWhenDoneChanged(bool value) => Save(() => _settings.OpenFolderWhenDone = value);

    partial void OnOpenResultsWhenDoneChanged(bool value) => Save(() => _settings.OpenResultsWhenDone = value);

    private void Save(Action write)
    {
        try
        {
            write();
            NotificationsStatus = string.Empty;
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving a notification setting: {ex.GetType().Name}");
            NotificationsStatus = _strings.GetString("NotificationsNotSaved");
        }
    }
}
