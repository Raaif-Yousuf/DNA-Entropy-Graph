using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>The Settings "Notifications" group (issue #643): three switches, each saved the moment it changes through <see cref="NotificationSettings"/>, the object the completion path reads.</summary>
public sealed partial class NotificationsSettingsViewModel : ObservableObject
{
    private readonly NotificationSettings _settings;
    private readonly IStringResourceProvider _strings;
    private bool _loading;

    [ObservableProperty]
    private bool _notifyWhenDone;

    [ObservableProperty]
    private bool _openFolderWhenDone;

    [ObservableProperty]
    private bool _openResultsWhenDone;

    [ObservableProperty]
    private string _notificationsStatus = string.Empty;

    public NotificationsSettingsViewModel(NotificationSettings settings, IStringResourceProvider strings, IMessenger? messenger = null)
    {
        _settings = settings;
        _strings = strings;

        // Fields, not properties: opening the page must not save what was just read.
        _notifyWhenDone = settings.NotifyWhenDone;
        _openFolderWhenDone = settings.OpenFolderWhenDone;
        _openResultsWhenDone = settings.OpenResultsWhenDone;
        messenger?.Register<NotificationsSettingsViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
    }

    /// <summary>Re-reads the three switches after Reset all settings, without saving what was just read.</summary>
    private void Reload()
    {
        _loading = true;
        try
        {
            NotifyWhenDone = _settings.NotifyWhenDone;
            OpenFolderWhenDone = _settings.OpenFolderWhenDone;
            OpenResultsWhenDone = _settings.OpenResultsWhenDone;
            NotificationsStatus = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnNotifyWhenDoneChanged(bool value) => Save(() => _settings.NotifyWhenDone = value);

    partial void OnOpenFolderWhenDoneChanged(bool value) => Save(() => _settings.OpenFolderWhenDone = value);

    partial void OnOpenResultsWhenDoneChanged(bool value) => Save(() => _settings.OpenResultsWhenDone = value);

    private void Save(Action write)
    {
        if (_loading)
        {
            return;
        }

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
