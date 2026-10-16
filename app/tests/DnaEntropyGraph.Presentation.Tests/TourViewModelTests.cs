using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #143: the first-launch tour walks New run, Runs, Cloud and Settings once, is dismissable, remembers it was seen, and can be replayed.</summary>
public sealed class TourViewModelTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool ThrowOnRead { get; set; }

        public bool ThrowOnWrite { get; set; }

        public string? GetString(string key)
            => ThrowOnRead ? throw new SettingsUnavailableException("locked") : Values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            if (ThrowOnWrite)
            {
                throw new SettingsUnavailableException("locked");
            }

            Values[key] = value;
        }
    }

    private static TourViewModel Create(MemorySettings store) => new(new TourSettings(store));

    private static bool[] Open(TourViewModel tour) => [tour.IsNewRunStepOpen, tour.IsRunsStepOpen, tour.IsCloudStepOpen, tour.IsSettingsStepOpen];

    [Fact]
    public void A_fresh_profile_opens_the_first_step_only_at_launch()
    {
        var tour = Create(new MemorySettings());

        tour.StartIfFirstLaunch();

        Open(tour).ShouldBe([true, false, false, false]);
    }

    [Fact]
    public void Next_walks_the_four_steps_in_order_and_the_last_one_finishes_and_remembers()
    {
        var store = new MemorySettings();
        var tour = Create(store);
        tour.StartIfFirstLaunch();

        tour.NextCommand.Execute(null);
        Open(tour).ShouldBe([false, true, false, false]);
        tour.NextCommand.Execute(null);
        Open(tour).ShouldBe([false, false, true, false]);
        tour.NextCommand.Execute(null);
        Open(tour).ShouldBe([false, false, false, true]);
        tour.NextCommand.Execute(null);

        Open(tour).ShouldBe([false, false, false, false]);
        tour.IsRunning.ShouldBeFalse();
        store.Values[TourSettings.CompletedKey].ShouldBe("true");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Skip_closes_the_tour_at_any_step_and_remembers(int stepsToAdvance)
    {
        var store = new MemorySettings();
        var tour = Create(store);
        tour.StartIfFirstLaunch();
        for (var i = 0; i < stepsToAdvance; i++)
        {
            tour.NextCommand.Execute(null);
        }

        tour.SkipCommand.Execute(null);

        Open(tour).ShouldBe([false, false, false, false]);
        store.Values[TourSettings.CompletedKey].ShouldBe("true");
    }

    [Fact]
    public void A_profile_that_has_seen_the_tour_is_not_shown_it_again()
    {
        var store = new MemorySettings();
        store.Values[TourSettings.CompletedKey] = "true";
        var tour = Create(store);

        tour.StartIfFirstLaunch();

        tour.IsRunning.ShouldBeFalse();
        Open(tour).ShouldBe([false, false, false, false]);
    }

    [Fact]
    public void Replay_starts_from_the_first_step_even_when_the_tour_was_seen()
    {
        var store = new MemorySettings();
        store.Values[TourSettings.CompletedKey] = "true";
        var tour = Create(store);

        tour.StartCommand.Execute(null);

        Open(tour).ShouldBe([true, false, false, false]);
        tour.SkipCommand.Execute(null);
        store.Values[TourSettings.CompletedKey].ShouldBe("true");
    }

    [Fact]
    public void Starting_while_running_restarts_at_step_one_and_never_leaves_two_tips_open()
    {
        var tour = Create(new MemorySettings());
        tour.StartIfFirstLaunch();
        tour.NextCommand.Execute(null);

        tour.StartCommand.Execute(null);

        Open(tour).ShouldBe([true, false, false, false]);
    }

    [Fact]
    public void An_unreadable_settings_file_does_not_nag_and_an_unwritable_one_does_not_crash_the_skip()
    {
        var locked = new MemorySettings { ThrowOnRead = true };
        var lockedTour = Create(locked);
        lockedTour.StartIfFirstLaunch();
        lockedTour.IsRunning.ShouldBeFalse();

        var store = new MemorySettings { ThrowOnWrite = true };
        var tour = Create(store);
        tour.StartIfFirstLaunch();
        Should.NotThrow(() => tour.SkipCommand.Execute(null));
        tour.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void Next_and_Skip_with_no_tour_running_do_nothing()
    {
        var store = new MemorySettings();
        var tour = Create(store);

        tour.NextCommand.Execute(null);
        tour.SkipCommand.Execute(null);

        Open(tour).ShouldBe([false, false, false, false]);
        store.Values.ShouldBeEmpty();
    }
}
