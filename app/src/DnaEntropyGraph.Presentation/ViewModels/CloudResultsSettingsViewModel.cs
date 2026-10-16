using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One length in the "Keep results for" list.</summary>
public sealed record RetentionChoice(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Settings "Cloud results" group (issue #114): how long Google keeps each run's stored results. It writes
/// <see cref="ResultsRetentionSetting.Key"/>, which the storage gateway reads at the start of every run (so a change applies to the
/// next run, not to one in progress). Choosing fewer days than the bucket already keeps does not shrink it (#597), and the page says so.
/// </summary>
public sealed partial class CloudResultsSettingsViewModel : ObservableObject
{
    private static readonly int[] Presets = [7, 30, 90, 180, 365];

    private readonly ISettingsStore _settings;
    private readonly IStringResourceProvider _strings;
    private bool _loading;

    [ObservableProperty]
    private RetentionChoice _selectedChoice;

    [ObservableProperty]
    private string _retentionStatus = string.Empty;

    public CloudResultsSettingsViewModel(ISettingsStore settings, IStringResourceProvider strings, IMessenger? messenger = null)
    {
        _settings = settings;
        _strings = strings;
        var days = ResultsRetentionSetting.Days(settings);
        BuildChoices(days);
        // Field, not property: opening the page must not save what was just read.
        _selectedChoice = Choices.Single(c => c.Days == days);
        messenger?.Register<CloudResultsSettingsViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
    }

    /// <summary>The lengths offered, in order; a hand-edited value outside the presets is listed too, so the box never shows a length that is not in use.</summary>
    public ObservableCollection<RetentionChoice> Choices { get; } = [];

    private void BuildChoices(int days)
    {
        Choices.Clear();
        foreach (var d in Presets.Append(days).Distinct().Order())
        {
            Choices.Add(new RetentionChoice(d, string.Format(CultureInfo.CurrentCulture, _strings.GetString("CloudRetentionChoiceFormat"), d)));
        }
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            var days = ResultsRetentionSetting.Days(_settings);
            BuildChoices(days);
            SelectedChoice = Choices.Single(c => c.Days == days);
            RetentionStatus = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSelectedChoiceChanged(RetentionChoice? oldValue, RetentionChoice newValue)
    {
        var value = newValue;
        // A ComboBox sets null while its list is rebuilt.
        if (_loading || value is null)
        {
            return;
        }

        try
        {
            _settings.SetString(ResultsRetentionSetting.Key, value.Days.ToString(CultureInfo.InvariantCulture));
            // The bucket's current age is not known here, and a shorter choice never shrinks a longer rule (#597): say so instead of "Saved" alone.
            RetentionStatus = _strings.GetString(oldValue is not null && value.Days < oldValue.Days ? "CloudRetentionSavedShorter" : "CloudRetentionSaved");
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving the results retention: {ex.GetType().Name}");
            RetentionStatus = _strings.GetString("AdvancedNotSaved");
        }
    }
}
