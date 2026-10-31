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

    [Fact]
    public void Choosing_a_palette_invalidates_the_provider_so_the_next_draw_uses_it()
    {
        var store = new DictionaryStore();
        var provider = new SettingsTrackPaletteProvider(store);
        provider.Current.Id.ShouldBe(TrackPalettes.DefaultId); // cached
        var viewModel = new PaletteSettingsViewModel(store, palettes: provider);

        viewModel.PaletteIndex = 1;

        provider.Current.Id.ShouldBe(TrackPalettes.HighContrastId);
    }

    [Fact]
    public void Reset_all_settings_and_a_settings_import_invalidate_the_provider_through_the_attached_listener()
    {
        var store = new DictionaryStore();
        var messenger = new WeakReferenceMessenger();
        var provider = new SettingsTrackPaletteProvider(store);
        PaletteCacheInvalidator.Attach(messenger, provider);
        store.Values[TrackPalettes.SettingKey] = TrackPalettes.HighContrastId;
        provider.Current.Id.ShouldBe(TrackPalettes.HighContrastId); // cached

        store.Values.Clear(); // what Reset does; an import changes the value the same way
        messenger.Send(new SettingsResetMessage());

        provider.Current.Id.ShouldBe(TrackPalettes.DefaultId);
    }

    [Fact]
    public void A_settings_import_that_saved_high_contrast_puts_the_radio_on_it_without_saving()
    {
        var store = new DictionaryStore();
        var messenger = new WeakReferenceMessenger();
        var provider = new SettingsTrackPaletteProvider(store);
        PaletteCacheInvalidator.Attach(messenger, provider);
        var viewModel = new PaletteSettingsViewModel(store, messenger, provider);
        viewModel.PaletteIndex.ShouldBe(0);
        var changes = 0;
        viewModel.PropertyChanged += (_, _) => changes++;
        store.Values[TrackPalettes.SettingKey] = TrackPalettes.HighContrastId; // what the import wrote; it sends the same message as Reset

        messenger.Send(new SettingsResetMessage());

        viewModel.PaletteIndex.ShouldBe(1);
        provider.Current.Id.ShouldBe(TrackPalettes.HighContrastId);
        store.Values[TrackPalettes.SettingKey].ShouldBe(TrackPalettes.HighContrastId); // untouched by the radio catching up
        changes.ShouldBe(1); // only the index change; the store value above is unchanged, so nothing was written back
    }

    [Fact]
    public void After_an_import_the_message_order_cannot_leave_the_radio_on_a_stale_choice()
    {
        // The viewmodel registered first and the invalidator second, and the other way round: the radio reads the store, not the cache.
        foreach (var viewModelFirst in new[] { true, false })
        {
            var store = new DictionaryStore();
            var messenger = new WeakReferenceMessenger();
            var provider = new SettingsTrackPaletteProvider(store);
            provider.Current.Id.ShouldBe(TrackPalettes.DefaultId); // cached
            PaletteSettingsViewModel viewModel;
            if (viewModelFirst)
            {
                viewModel = new PaletteSettingsViewModel(store, messenger, provider);
                PaletteCacheInvalidator.Attach(messenger, provider);
            }
            else
            {
                PaletteCacheInvalidator.Attach(messenger, provider);
                viewModel = new PaletteSettingsViewModel(store, messenger, provider);
            }

            store.Values[TrackPalettes.SettingKey] = TrackPalettes.HighContrastId;
            messenger.Send(new SettingsResetMessage());

            viewModel.PaletteIndex.ShouldBe(1, $"viewModelFirst={viewModelFirst}");
            provider.Current.Id.ShouldBe(TrackPalettes.HighContrastId, $"viewModelFirst={viewModelFirst}");
        }
    }

    private sealed class DictionaryStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }
}
