using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Viewers;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "External viewers" group (issue #641): where IGV and Geneious are, and IGV's batch port. Every value is
/// read and written through <see cref="ViewerSettingKeys"/>, the same keys <see cref="ExternalViewerOpener"/> reads at each
/// press, so a change here is the next Open in IGV's behaviour with no copy in between. A path left empty means "find it
/// automatically"; a port left at the default is <see cref="IgvBatchCommands.DefaultPort"/>.
/// </summary>
public sealed partial class ExternalViewersSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IFilePicker _picker;
    private readonly IStringResourceProvider _strings;

    [ObservableProperty]
    private string _igvPath;

    [ObservableProperty]
    private string _geneiousPath;

    [ObservableProperty]
    private string _igvPortText;

    /// <summary>Why the IGV path is flagged ("no program at this path"), or empty. Never blocks the save: the path may be on a drive that is not mounted yet.</summary>
    [ObservableProperty]
    private string _igvPathStatus = string.Empty;

    [ObservableProperty]
    private string _geneiousPathStatus = string.Empty;

    /// <summary>Why the typed port was refused, or empty.</summary>
    [ObservableProperty]
    private string _igvPortStatus = string.Empty;

    public ExternalViewersSettingsViewModel(ISettingsStore settings, IFilePicker picker, IStringResourceProvider strings)
    {
        _settings = settings;
        _picker = picker;
        _strings = strings;

        // Fields, not properties: opening the page must not save what was just read.
        _igvPath = Read(ViewerSettingKeys.IgvPath) ?? string.Empty;
        _geneiousPath = Read(ViewerSettingKeys.GeneiousPath) ?? string.Empty;
        _igvPortText = (TryParsePort(Read(ViewerSettingKeys.IgvPort), out var port) ? port : IgvBatchCommands.DefaultPort)
            .ToString(CultureInfo.InvariantCulture);
        _igvPathStatus = PathStatus(_igvPath);
        _geneiousPathStatus = PathStatus(_geneiousPath);
    }

    partial void OnIgvPathChanged(string value) => IgvPathStatus = SavePath(ViewerSettingKeys.IgvPath, value);

    partial void OnGeneiousPathChanged(string value) => GeneiousPathStatus = SavePath(ViewerSettingKeys.GeneiousPath, value);

    partial void OnIgvPortTextChanged(string value)
    {
        if (!TryParsePort(value, out var port))
        {
            // Not saved: the last good port stays in effect, and the message says what to type.
            IgvPortStatus = _strings.GetString("ViewerPortInvalid");
            return;
        }

        IgvPortStatus = Write(ViewerSettingKeys.IgvPort, port.ToString(CultureInfo.InvariantCulture)) ? string.Empty : _strings.GetString("ViewerSettingNotSaved");
    }

    [RelayCommand]
    private async Task BrowseIgvAsync(CancellationToken cancellationToken) => await BrowseAsync(path => IgvPath = path, cancellationToken);

    [RelayCommand]
    private async Task BrowseGeneiousAsync(CancellationToken cancellationToken) => await BrowseAsync(path => GeneiousPath = path, cancellationToken);

    [RelayCommand]
    private void ResetIgvPath() => IgvPath = string.Empty;

    [RelayCommand]
    private void ResetGeneiousPath() => GeneiousPath = string.Empty;

    [RelayCommand]
    private void ResetIgvPort()
    {
        var text = IgvBatchCommands.DefaultPort.ToString(CultureInfo.InvariantCulture);
        if (IgvPortText == text)
        {
            // Already the default text, so no change event: clear a stale refusal and make sure the saved value says default.
            IgvPortStatus = Write(ViewerSettingKeys.IgvPort, text) ? string.Empty : _strings.GetString("ViewerSettingNotSaved");
            return;
        }

        IgvPortText = text;
    }

    private async Task BrowseAsync(Action<string> apply, CancellationToken cancellationToken)
    {
        var picked = await _picker.PickProgramAsync(cancellationToken);
        if (!string.IsNullOrEmpty(picked))
        {
            apply(picked);
        }
    }

    /// <summary>Saves the trimmed path and returns the status text: empty when fine, a warning when no file is there.</summary>
    private string SavePath(string key, string value)
    {
        // A path pasted from Explorer's "Copy as path" arrives in quotes.
        var path = value.Trim().Trim('"').Trim();
        if (!Write(key, path))
        {
            return _strings.GetString("ViewerSettingNotSaved");
        }

        return PathStatus(path);
    }

    private string PathStatus(string path)
        => string.IsNullOrWhiteSpace(path) || File.Exists(path) ? string.Empty : _strings.GetString("ViewerPathMissing");

    private static bool TryParsePort(string? text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    // #558: a locked settings file must never crash the page; the value just is not remembered, and the row says so.
    private string? Read(string key)
    {
        try
        {
            return _settings.GetString(key);
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while reading a viewer setting: {ex.GetType().Name}");
            return null;
        }
    }

    private bool Write(string key, string value)
    {
        try
        {
            _settings.SetString(key, value);
            return true;
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving a viewer setting: {ex.GetType().Name}");
            return false;
        }
    }
}
