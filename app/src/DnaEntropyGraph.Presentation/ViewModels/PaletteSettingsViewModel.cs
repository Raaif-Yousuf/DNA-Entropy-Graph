using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Palettes;
using DnaEntropyGraph.Presentation.Messaging;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Settings > Appearance > track colours (issue #195). The radio's position is the index into <see cref="TrackPalettes.All"/>; choosing one
/// saves its id under <see cref="TrackPalettes.SettingKey"/>. Nothing is pushed anywhere: a save only tells <see cref="ITrackPaletteProvider"/> to forget its cached
/// choice, and the chart, the sparkline, the PNG export and the viewer read it from there the next time they draw.
/// </summary>
public sealed partial class PaletteSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly ITrackPaletteProvider? _palettes;
    private bool _catchingUp;

    [ObservableProperty]
    private int _paletteIndex;

    public PaletteSettingsViewModel(ISettingsStore settings, IMessenger? messenger = null, ITrackPaletteProvider? palettes = null)
    {
        _settings = settings;
        _palettes = palettes;
        // The field, not the property: opening the page must not save what was just read.
        _paletteIndex = IndexOf(new SettingsTrackPaletteProvider(settings).Current);

        // Reset all settings (#677) empties the file, so the radio goes back to the default without saving it again.
        messenger?.Register<PaletteSettingsViewModel, SettingsResetMessage>(this, static (vm, _) => vm.ShowDefault());
    }

    private static int IndexOf(TrackPalette palette)
    {
        for (var i = 0; i < TrackPalettes.All.Count; i++)
        {
            if (TrackPalettes.All[i].Id == palette.Id)
            {
                return i;
            }
        }

        return 0;
    }

    private void ShowDefault()
    {
        _catchingUp = true;
        try
        {
            PaletteIndex = 0;
        }
        finally
        {
            _catchingUp = false;
        }
    }

    partial void OnPaletteIndexChanged(int value)
    {
        if (_catchingUp || value < 0 || value >= TrackPalettes.All.Count)
        {
            // -1 is what a RadioButtons control reports when nothing is selected: there is no palette to save.
            return;
        }

        try
        {
            _settings.SetString(TrackPalettes.SettingKey, TrackPalettes.All[value].Id);
            _palettes?.Invalidate();
        }
        catch (SettingsUnavailableException ex)
        {
            // #558: a locked settings file must not crash the page; the choice is simply not remembered this time.
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving palette: {ex.GetType().Name}");
        }
    }
}
