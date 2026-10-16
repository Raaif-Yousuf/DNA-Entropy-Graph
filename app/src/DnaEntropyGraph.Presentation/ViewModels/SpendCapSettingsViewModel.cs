using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "Spending" group (issue #109): a monthly warning cap in US dollars, a switch that turns the warning into a hard stop,
/// and this month's estimated spend. Saved at once under <see cref="SpendCapSettings"/>'s keys, which the run start and the New run
/// page read on every check. An empty box turns the cap off. The month is the calendar month in this PC's time zone.
/// </summary>
public sealed partial class SpendCapSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IStringResourceProvider _strings;
    private readonly ISpendCapGate _gate;
    private bool _loading;

    [ObservableProperty]
    private string _capText;

    [ObservableProperty]
    private bool _isHardStop;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _monthToDateText = string.Empty;

    public SpendCapSettingsViewModel(ISettingsStore settings, IStringResourceProvider strings, ISpendCapGate gate, IMessenger? messenger = null)
    {
        _settings = settings;
        _strings = strings;
        _gate = gate;
        // Fields, not properties: opening the page must not save what was just read.
        _capText = FormatCap(SafeCap());
        _isHardStop = SafeHardStop();
        messenger?.Register<SpendCapSettingsViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
        _ = RefreshMonthToDateAsync();
    }

    partial void OnCapTextChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        var text = value.Trim();
        if (text.Length > 0 && !SpendCapSettings.TryParseCap(text, out _))
        {
            Status = _strings.GetString("SpendCapInvalid");
            return;
        }

        Save(SpendCapSettings.CapUsdKey, text);
    }

    partial void OnIsHardStopChanged(bool value)
    {
        if (!_loading)
        {
            Save(SpendCapSettings.HardStopKey, value ? "true" : "false");
        }
    }

    private void Save(string key, string value)
    {
        try
        {
            _settings.SetString(key, value);
            Status = string.Empty;
            _ = RefreshMonthToDateAsync();
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving the spending cap: {ex.GetType().Name}");
            Status = _strings.GetString("AdvancedNotSaved");
        }
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            CapText = FormatCap(SafeCap());
            IsHardStop = SafeHardStop();
            Status = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RefreshMonthToDateAsync()
    {
        try
        {
            var spent = await _gate.MonthToDateUsdAsync(CancellationToken.None);
            MonthToDateText = string.Format(
                CultureInfo.CurrentCulture,
                _strings.GetString("SpendMonthToDate"),
                spent.ToString("0.00", CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            MonthToDateText = string.Empty;
        }
    }

    private double? SafeCap()
    {
        try
        {
            return SpendCapSettings.ReadCapUsd(_settings);
        }
        catch (SettingsUnavailableException)
        {
            return null;
        }
    }

    private bool SafeHardStop()
    {
        try
        {
            return SpendCapSettings.IsHardStop(_settings);
        }
        catch (SettingsUnavailableException)
        {
            return false;
        }
    }

    private static string FormatCap(double? cap) => cap?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
}
