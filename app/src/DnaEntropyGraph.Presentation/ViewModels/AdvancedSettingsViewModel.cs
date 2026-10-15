using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
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
        ICloudModeInfo mode)
    {
        _settings = settings;
        _resettable = resettable;
        _dialogs = dialogs;
        _strings = strings;
        _mode = mode;
        IsDeveloperMode = string.Equals(Read(WorkerImageSettingKeys.DeveloperMode)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        _workerImage = IsDeveloperMode ? Read(WorkerImageSettingKeys.WorkerImage) ?? string.Empty : string.Empty;
    }

    /// <summary>True when settings.json already says <c>developer_mode</c>; the image box is only usable then.</summary>
    public bool IsDeveloperMode { get; }

    /// <summary>The note on which cloud the app uses.</summary>
    public string FakeCloudText => _strings.GetString(_mode.IsFakeCloud ? "FakeCloudOn" : "FakeCloudOff");

    partial void OnWorkerImageChanged(string value)
    {
        var image = value.Trim();
        if (image != value)
        {
            WorkerImage = image;
            return;
        }

        if (image.Length > 0 && !StartupMetadata.IsPinnedImageReference(image))
        {
            WorkerImageStatus = _strings.GetString("WorkerImageNotPinned");
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
            ResetStatus = _strings.GetString("AdvancedResetDone");
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while resetting settings: {ex.GetType().Name}");
            ResetStatus = _strings.GetString("AdvancedNotSaved");
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
