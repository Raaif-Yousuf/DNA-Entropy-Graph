using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Palettes;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Palettes;

public class TrackPaletteTests
{
    [Fact]
    public void The_default_palette_is_okabe_ito()
    {
        // Okabe and Ito (2008), "Color Universal Design": blue 0072B2, vermillion D55E00, bluish green 009E73, reddish purple CC79A7, sky blue 56B4E9, orange E69F00.
        var light = TrackPalettes.Default.Light;
        light.Entropy.ShouldBe("#0072B2");
        light.Forward.ShouldBe("#D55E00");
        light.Reverse.ShouldBe("#009E73");
        light.Smoothed.ShouldBe("#CC79A7");
        TrackPalettes.Default.Dark.Entropy.ShouldBe("#56B4E9");
        TrackPalettes.Default.Dark.Forward.ShouldBe("#E69F00");
    }

    [Theory]
    [InlineData(null, TrackPalettes.DefaultId)]
    [InlineData("", TrackPalettes.DefaultId)]
    [InlineData("nonsense", TrackPalettes.DefaultId)]
    [InlineData("default", TrackPalettes.DefaultId)]
    [InlineData("high-contrast", TrackPalettes.HighContrastId)]
    public void Resolve_maps_a_saved_id_and_anything_unknown_to_the_default(string? saved, string expected)
        => TrackPalettes.Resolve(saved).Id.ShouldBe(expected);

    [Fact]
    public void Every_colour_is_a_six_digit_hex_and_roles_are_all_covered()
    {
        foreach (var palette in TrackPalettes.All)
        {
            foreach (var dark in new[] { false, true })
            {
                foreach (var role in Enum.GetValues<TrackRole>())
                {
                    palette.Hex(role, dark).ShouldMatch("^#[0-9A-F]{6}$");
                }
            }
        }
    }

    [Fact]
    public void Marker_rgba_carries_the_marker_colour_and_the_alpha()
        => TrackPalettes.Default.Light.MarkerRgba(0.6).ShouldBe("rgba(0, 0, 0, 0.6)");

    [Fact]
    public void Many_reads_cost_one_settings_read()
    {
        var store = Substitute.For<ISettingsStore>();
        store.GetString(TrackPalettes.SettingKey).Returns("high-contrast");
        var provider = new SettingsTrackPaletteProvider(store);

        for (var i = 0; i < 200; i++)
        {
            provider.Current.Id.ShouldBe(TrackPalettes.HighContrastId);
        }

        store.Received(1).GetString(TrackPalettes.SettingKey);
    }

    [Fact]
    public void After_invalidate_the_next_read_sees_the_new_palette()
    {
        var store = Substitute.For<ISettingsStore>();
        var provider = new SettingsTrackPaletteProvider(store);
        store.GetString(TrackPalettes.SettingKey).Returns((string?)null);
        provider.Current.Id.ShouldBe(TrackPalettes.DefaultId);

        store.GetString(TrackPalettes.SettingKey).Returns("high-contrast");
        provider.Current.Id.ShouldBe(TrackPalettes.DefaultId); // still the cached one: nobody said it changed
        provider.Invalidate();

        provider.Current.Id.ShouldBe(TrackPalettes.HighContrastId);
    }

    [Fact]
    public void A_locked_read_is_not_remembered_so_the_next_read_tries_again()
    {
        var store = Substitute.For<ISettingsStore>();
        var calls = 0;
        store.GetString(TrackPalettes.SettingKey).Returns(_ => ++calls == 1 ? throw new SettingsUnavailableException("locked") : "high-contrast");
        var provider = new SettingsTrackPaletteProvider(store);

        provider.Current.Id.ShouldBe(TrackPalettes.DefaultId);
        provider.Current.Id.ShouldBe(TrackPalettes.HighContrastId);
    }

    [Fact]
    public void A_locked_settings_file_gives_the_default_palette()
    {
        var store = Substitute.For<ISettingsStore>();
        store.GetString(Arg.Any<string>()).Returns(_ => throw new SettingsUnavailableException("locked"));

        new SettingsTrackPaletteProvider(store).Current.Id.ShouldBe(TrackPalettes.DefaultId);
    }
}
