using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Palettes;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #195: Settings > Appearance > track colours.</summary>
public class PaletteSettingsViewModelTests
{
    [Fact]
    public void Nothing_saved_shows_the_default_palette()
    {
        var store = Substitute.For<ISettingsStore>();
        store.GetString(TrackPalettes.SettingKey).Returns((string?)null);

        new PaletteSettingsViewModel(store).PaletteIndex.ShouldBe(0);
    }

    [Fact]
    public void A_saved_high_contrast_choice_is_shown_when_the_page_opens()
    {
        var store = Substitute.For<ISettingsStore>();
        store.GetString(TrackPalettes.SettingKey).Returns(TrackPalettes.HighContrastId);

        new PaletteSettingsViewModel(store).PaletteIndex.ShouldBe(1);
    }

    [Fact]
    public void Choosing_a_palette_saves_its_id_and_the_provider_then_returns_it()
    {
        var store = new DictionaryStore();
        var viewModel = new PaletteSettingsViewModel(store);

        viewModel.PaletteIndex = 1;

        store.GetString(TrackPalettes.SettingKey).ShouldBe(TrackPalettes.HighContrastId);
        new SettingsTrackPaletteProvider(store).Current.Id.ShouldBe(TrackPalettes.HighContrastId);
    }

    [Fact]
    public void Opening_the_page_does_not_write_the_setting()
    {
        var store = Substitute.For<ISettingsStore>();
        store.GetString(TrackPalettes.SettingKey).Returns(TrackPalettes.HighContrastId);

        _ = new PaletteSettingsViewModel(store);

        store.DidNotReceive().SetString(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void A_locked_settings_file_neither_crashes_opening_nor_choosing()
    {
        var store = Substitute.For<ISettingsStore>();
        store.GetString(Arg.Any<string>()).Returns(_ => throw new SettingsUnavailableException("locked"));
        store.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));

        var viewModel = new PaletteSettingsViewModel(store);
        viewModel.PaletteIndex.ShouldBe(0);

        Should.NotThrow(() => viewModel.PaletteIndex = 1);
        viewModel.PaletteIndex.ShouldBe(1);
    }

    [Fact]
    public void An_out_of_range_selection_is_ignored()
    {
        var store = new DictionaryStore();
        var viewModel = new PaletteSettingsViewModel(store);

        viewModel.PaletteIndex = -1;

        store.GetString(TrackPalettes.SettingKey).ShouldBeNull();
    }

    [Fact]
    public void Reset_all_settings_puts_the_radio_back_on_the_default_without_saving()
    {
        var store = new DictionaryStore();
        var messenger = new WeakReferenceMessenger();
        var viewModel = new PaletteSettingsViewModel(store, messenger);
        viewModel.PaletteIndex = 1;
        store.Values.Clear();

        messenger.Send(new SettingsResetMessage());

        viewModel.PaletteIndex.ShouldBe(0);
        store.GetString(TrackPalettes.SettingKey).ShouldBeNull();
    }

    private sealed class DictionaryStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }
}
