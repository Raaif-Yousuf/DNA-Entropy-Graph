using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "Advanced" group (issue #644): a developer's worker image, a read-only note on whether the cloud is the
/// development fake, and Reset all settings. The image goes to <see cref="WorkerImageSettingKeys.WorkerImage"/>, the key the run
/// start reads on every press (PinnedWorkerImageProvider), and the box is live only when developer mode is already on in
/// settings.json: the digest allowlist (CLAUDE.md Stack, Container row) is not something the app lets a lab user switch off.
/// </summary>
public sealed partial class AdvancedSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IResettableSettings _resettable;
    private readonly IDialogService _dialogs;
    private readonly IStringResourceProvider _strings;
    private readonly ICloudModeInfo _mode;
    private readonly IMessenger? _messenger;
    private bool _loading;

    /// <summary>True when settings.json already says <c>developer_mode</c>; the image box is only usable then. Re-read after Reset all settings.</summary>
    [ObservableProperty]
    private bool _isDeveloperMode;

    [ObservableProperty]
    private string _workerImage;

    [ObservableProperty]
    private string _workerImageStatus = string.Empty;

    [ObservableProperty]
    private string _resetStatus = string.Empty;

    public AdvancedSettingsViewModel(
        ISettingsStore settings,
        IResettableSettings resettable,
        IDialogService dialogs,
        IStringResourceProvider strings,
        ICloudModeInfo mode,
        IMessenger? messenger = null)
    {
        _settings = settings;
        _resettable = resettable;
        _dialogs = dialogs;
        _strings = strings;
        _mode = mode;
        _messenger = messenger;
        // Fields, not properties: opening the page must not save what was just read.
        _isDeveloperMode = ReadDeveloperMode();
        _workerImage = _isDeveloperMode ? Read(WorkerImageSettingKeys.WorkerImage) ?? string.Empty : string.Empty;
        messenger?.Register<AdvancedSettingsViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
    }

    private bool ReadDeveloperMode()
        => string.Equals(Read(WorkerImageSettingKeys.DeveloperMode)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-reads developer mode and the image after Reset all settings, without saving what was just read.</summary>
    private void Reload()
    {
        _loading = true;
        try
        {
            IsDeveloperMode = ReadDeveloperMode();
            WorkerImage = IsDeveloperMode ? Read(WorkerImageSettingKeys.WorkerImage) ?? string.Empty : string.Empty;
            WorkerImageStatus = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The note on which cloud the app uses.</summary>
    public string FakeCloudText => _strings.GetString(_mode.IsFakeCloud ? "FakeCloudOn" : "FakeCloudOff");

    partial void OnWorkerImageChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        var image = value.Trim();
        if (image != value)
        {
            WorkerImage = image;
            return;
        }

        if (image.Length > 0 && !StartupMetadata.IsPinnedImageReference(image))
        {
            // Not saved, and the override saved before is cleared: the box shows text that is not in use, so the shipped image
            // must be the one in use, never the older override the box no longer shows.
            WorkerImageStatus = ClearSavedOverride() ? _strings.GetString("WorkerImageNotPinned") : _strings.GetString("AdvancedNotSaved");
            return;
        }

        try
        {
            _settings.SetString(WorkerImageSettingKeys.WorkerImage, image);
            WorkerImageStatus = string.Empty;
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving the worker image: {ex.GetType().Name}");
            WorkerImageStatus = _strings.GetString("AdvancedNotSaved");
        }
    }

    [RelayCommand]
    private async Task ResetAllSettingsAsync(CancellationToken cancellationToken)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            _strings.GetString("AdvancedResetConfirmTitle"),
            _strings.GetString("AdvancedResetConfirmBody"),
            cancellationToken);
        if (!confirmed)
        {
            return;
        }

        try
        {
            _resettable.ResetAll();
            _messenger?.Send(new SettingsResetMessage());
            ResetStatus = _strings.GetString("AdvancedResetDone");
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while resetting settings: {ex.GetType().Name}");
            ResetStatus = _strings.GetString("AdvancedNotSaved");
        }
    }

    /// <summary>Empties a saved override, if there is one. False only when the settings file is locked.</summary>
    private bool ClearSavedOverride()
    {
        try
        {
            if (!string.IsNullOrEmpty(_settings.GetString(WorkerImageSettingKeys.WorkerImage)))
            {
                _settings.SetString(WorkerImageSettingKeys.WorkerImage, string.Empty);
            }

            return true;
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while clearing the worker image: {ex.GetType().Name}");
            return false;
        }
    }

    private string? Read(string key)
    {
        try
        {
            return _settings.GetString(key);
        }
        catch (SettingsUnavailableException)
        {
            return null;
        }
    }
}
