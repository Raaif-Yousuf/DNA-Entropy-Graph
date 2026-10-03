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

    // Issue #489: MEASURED 2026-10-02, minimizing the real window saved "-32000,-32000,391,61".
    [Theory]
    [InlineData("-32000,-32000,391,61")]
    [InlineData("-32000,100,1280,800")]
    [InlineData("100,-32000,1280,800")]
    [InlineData("100,100,319,800")]
    [InlineData("100,100,1280,199")]
    public void A_degenerate_or_minimized_placement_falls_back_to_the_default(string raw)
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString("MainWindowPlacement").Returns(raw);
        var service = new WindowPlacementService(settingsStore);

        service.Load().ShouldBe(WindowPlacement.Default);
        service.TryLoad().ShouldBeNull();
    }

    [Fact]
    public void A_normal_placement_with_a_negative_position_on_a_left_hand_monitor_is_still_accepted()
    {
        WindowPlacement.Parse("-1500,40,1000,700").ShouldBe(new WindowPlacement(-1500, 40, 1000, 700));
    }

    [Fact]
    public void A_locked_settings_file_never_throws_from_Load_or_from_Save_on_close()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.GetString(Arg.Any<string>()).Returns(_ => throw new SettingsUnavailableException("locked"));
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new SettingsUnavailableException("locked"));
        var service = new WindowPlacementService(settingsStore);

        service.TryLoad().ShouldBeNull();
        service.Load().ShouldBe(WindowPlacement.Default);
        Should.NotThrow(() => service.Save(new WindowPlacement(10, 10, 800, 600)));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Save_on_close_is_best_effort_whatever_IO_failure_the_store_raises(Type failure)
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw (Exception)Activator.CreateInstance(failure, "denied")!);
        var service = new WindowPlacementService(settingsStore);

        Should.NotThrow(() => service.Save(new WindowPlacement(10, 10, 800, 600)));
        settingsStore.ReceivedWithAnyArgs(1).SetString(default!, default!);
    }

    [Fact]
    public void Save_refuses_a_minimized_placement_and_keeps_the_last_good_one()
    {
        var settingsStore = new FakeSettingsStore();
        var service = new WindowPlacementService(settingsStore);
        var good = new WindowPlacement(300, 200, 1000, 700);
        service.Save(good);

        service.Save(new WindowPlacement(-32000, -32000, 391, 61));

        service.Load().ShouldBe(good);
    }

    [Fact]
    public void TryLoad_is_null_on_a_fresh_profile_so_the_caller_can_scale_the_default_for_DPI()
    {
        new WindowPlacementService(new FakeSettingsStore()).TryLoad().ShouldBeNull();
    }

    // Issue #490 follow-up (item 4): the default is 1280x800 DIPs, so physical pixels = DIPs * scale.
    [Theory]
    [InlineData(1.0, 1280, 800)]
    [InlineData(1.5, 1920, 1200)]
    [InlineData(2.5, 3200, 2000)]
    public void The_default_is_scaled_by_the_window_DPI_scale_to_stay_1280_by_800_DIPs(double scale, int width, int height)
    {
        var scaled = WindowPlacement.Default.Scaled(scale);

        scaled.Width.ShouldBe(width);
        scaled.Height.ShouldBe(height);
        scaled.X.ShouldBe((int)Math.Round(WindowPlacement.Default.X * scale));
    }

    [Fact]
    public void FitInto_shrinks_a_placement_larger_than_the_work_area_and_moves_it_inside()
    {
        // MEASURED 2026-10-02: 1280x800 DIPs at 250% is 3200x2000 px, larger than this laptop's work area.
        var fitted = WindowPlacement.Default.Scaled(2.5).FitInto(0, 0, 2950, 1750);

        fitted.X.ShouldBeGreaterThanOrEqualTo(0);
        fitted.Y.ShouldBeGreaterThanOrEqualTo(0);
        (fitted.X + fitted.Width).ShouldBeLessThanOrEqualTo(2950);
        (fitted.Y + fitted.Height).ShouldBeLessThanOrEqualTo(1750);
    }

    [Fact]
    public void FitInto_leaves_a_placement_that_already_fits_unchanged()
    {
        var placement = new WindowPlacement(100, 100, 1000, 700);

        placement.FitInto(0, 0, 1920, 1040).ShouldBe(placement);
    }

    [Fact]
    public void A_non_positive_scale_leaves_the_placement_unchanged()
    {
        WindowPlacement.Default.Scaled(0).ShouldBe(WindowPlacement.Default);
    }

    [Fact]
    public void A_placement_wholly_off_the_work_area_is_not_reachable_and_one_overlapping_it_is()
    {
        // Work area 0,0 1920x1040 (a monitor that was unplugged leaves a saved rect off to the right).
        new WindowPlacement(2500, 100, 1000, 700).IsReachableOn(0, 0, 1920, 1040).ShouldBeFalse();
        new WindowPlacement(100, 100, 1000, 700).IsReachableOn(0, 0, 1920, 1040).ShouldBeTrue();
        new WindowPlacement(1800, 100, 1000, 700).IsReachableOn(0, 0, 1920, 1040).ShouldBeTrue();
    }

    [Fact]
    public void An_unregistered_page_key_resolves_to_the_fallback_page_and_a_registered_one_to_its_own()
    {
        var navigation = new NavigationService();
        navigation.RegisterPage("RunProgress", typeof(string));
        navigation.RegisterFallbackPage(typeof(int));

        navigation.ResolvePageType("RunProgress").ShouldBe(typeof(string));
        navigation.ResolvePageType("runprogress").ShouldBe(typeof(string));
        navigation.ResolvePageType("Cloud").ShouldBe(typeof(int));
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void SetString(string key, string value) => _values[key] = value;
    }
}
