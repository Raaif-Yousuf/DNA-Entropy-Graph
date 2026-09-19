using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// Issue #62's "window size/position remembered across launches". No UI
/// thread or real window is involved here on purpose - this is the pure
/// round-trip logic through <see cref="ISettingsStore"/>, the same store
/// SettingsViewModel already reads/writes for Theme
/// (docs/architecture.md section 6: settings.json is one file for every
/// app setting, this is not a second one).
/// </summary>
public class WindowPlacementServiceTests
{
    [Fact]
    public void Load_returns_a_sensible_default_when_nothing_was_ever_saved()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("MainWindowPlacement").Returns((string?)null);
        var service = new WindowPlacementService(settingsStore);

        var placement = service.Load();

        placement.Width.ShouldBeGreaterThan(0);
        placement.Height.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Save_then_Load_round_trips_the_exact_placement()
    {
        var settingsStore = new FakeSettingsStore();
        var service = new WindowPlacementService(settingsStore);
        var saved = new WindowPlacement(120, 340, 1366, 900);

        service.Save(saved);
        var loaded = service.Load();

        loaded.ShouldBe(saved);
    }

    [Fact]
    public void A_corrupted_saved_value_falls_back_to_the_default_instead_of_throwing()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("MainWindowPlacement").Returns("not,a,valid,placement,at,all");
        var service = new WindowPlacementService(settingsStore);

        var placement = service.Load();

        placement.ShouldBe(WindowPlacement.Default);
    }

    [Fact]
    public void A_zero_or_negative_size_falls_back_to_the_default_rather_than_producing_an_invisible_window()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("MainWindowPlacement").Returns("0,0,0,0");
        var service = new WindowPlacementService(settingsStore);

        var placement = service.Load();

        placement.ShouldBe(WindowPlacement.Default);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void SetString(string key, string value) => _values[key] = value;
    }
}
