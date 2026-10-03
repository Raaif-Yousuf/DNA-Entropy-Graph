using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cost;

/// <summary>Issue #98: the arithmetic behind "about $0.13, about 9 minutes". Pure: a table and numbers in, a number out.</summary>
public sealed class CostEstimatorTests
{
    private const string L4 = "g2-standard-8";
    private const string A100 = "a2-highgpu-1g";

    // Hourly cost of the L4 tier while it runs: 0.85 for the machine and GPU plus 150 GB at 0.10 per GB-month over 730 hours.
    private const double L4PerHour = 0.85 + (150 * 0.10 / 730);

    private static readonly PricingTable Table = PricingTable.Parse(PricingTableTests.Good).Table!;

    private static CostEstimateResult Estimate(string machine = L4, bool spot = false, long? bases = 5_000, params double[] pastMinutes)
        => CostEstimator.Estimate(Table, RunTimeModel.Default, new CostEstimateRequest(machine, spot, bases, pastMinutes.Select(TimeSpan.FromMinutes).ToList()));

    [Fact]
    public void History_gives_one_point_estimate_from_the_median_duration_priced_at_the_hourly_rate()
    {
        var result = Estimate(bases: 5_000, pastMinutes: [8, 9, 30]);

        var estimate = result.Estimate.ShouldNotBeNull();
        estimate.Basis.ShouldBe(EstimateBasis.History);
        estimate.IsPoint.ShouldBeTrue();
        estimate.MinMinutes.ShouldBe(9, 1e-9);
        estimate.MinUsd.ShouldBe(L4PerHour * 9 / 60, 1e-9);
        estimate.MaxUsd.ShouldBe(estimate.MinUsd, 1e-12);
        Math.Round(estimate.MinUsd, 2).ShouldBe(0.13, "the issue's own example: about $0.13, about 9 minutes");
    }

    [Fact]
    public void An_even_number_of_past_runs_uses_the_mean_of_the_middle_two()
        => Estimate(pastMinutes: [8, 10]).Estimate!.MinMinutes.ShouldBe(9, 1e-9);

    [Fact]
    public void History_needs_no_size_because_the_past_runs_already_say_how_long_it_takes()
        => Estimate(bases: null, pastMinutes: [9]).Estimate!.Basis.ShouldBe(EstimateBasis.History);

    [Fact]
    public void With_no_history_the_model_gives_a_range_from_a_warm_to_a_fresh_computer_plus_predict_time()
    {
        var estimate = Estimate(bases: 5_000).Estimate.ShouldNotBeNull();

        // 5 kb at 2 s per kb is 10 s of predict time on top of 5 min (warm) or 12 min (fresh).
        var predictMinutes = 10.0 / 60;
        estimate.Basis.ShouldBe(EstimateBasis.Model);
        estimate.IsPoint.ShouldBeFalse();
        estimate.MinMinutes.ShouldBe(5 + predictMinutes, 1e-9);
        estimate.MaxMinutes.ShouldBe(12 + predictMinutes, 1e-9);
        estimate.MinUsd.ShouldBe(L4PerHour * (5 + predictMinutes) / 60, 1e-9);
        estimate.MaxUsd.ShouldBe(L4PerHour * (12 + predictMinutes) / 60, 1e-9);
    }

    [Fact]
    public void Predict_time_grows_with_the_sequence_length()
    {
        var small = Estimate(bases: 1_000).Estimate!;
        var large = Estimate(bases: 1_000_000).Estimate!;

        (large.MaxMinutes - small.MaxMinutes).ShouldBe(999_000 / 1000.0 * 2.0 / 60, 1e-6);
        large.MaxUsd.ShouldBeGreaterThan(small.MaxUsd);
    }

    [Fact]
    public void An_unknown_size_with_no_history_stays_unknown_and_is_never_guessed()
    {
        var result = Estimate(bases: null);

        result.Estimate.ShouldBeNull();
        result.Reason.ShouldBe(EstimateUnavailable.UnknownSize);
    }

    [Fact]
    public void Spot_uses_the_spot_rate_and_is_cheaper()
    {
        var onDemand = Estimate(spot: false, pastMinutes: [9]).Estimate!;
        var spot = Estimate(spot: true, pastMinutes: [9]).Estimate!;

        spot.MinUsd.ShouldBe((0.18 + (150 * 0.10 / 730)) * 9 / 60, 1e-9);
        spot.MinUsd.ShouldBeLessThan(onDemand.MinUsd);
    }

    [Fact]
    public void A_spot_price_that_is_unknown_is_unavailable_not_zero()
    {
        var result = Estimate(machine: A100, spot: true, pastMinutes: [9]);

        result.Estimate.ShouldBeNull();
        result.Reason.ShouldBe(EstimateUnavailable.NoSpotPrice);
    }

    [Fact]
    public void A_machine_with_no_price_is_unavailable()
    {
        var result = Estimate(machine: "a3-highgpu-1g", pastMinutes: [9]);

        result.Estimate.ShouldBeNull();
        result.Reason.ShouldBe(EstimateUnavailable.NoPriceForMachine);
    }

    [Fact]
    public void The_a100_tier_is_priced_from_its_own_row()
        => Estimate(machine: A100, pastMinutes: [60]).Estimate!.MinUsd.ShouldBe(3.67 + (150 * 0.10 / 730), 1e-9);

    private static RunRecord Run(JobPhase phase, string machine, int minutes, int daysAgo, bool finished = true) => new(
        "j" + Guid.NewGuid().ToString("N"),
        phase,
        DateTimeOffset.UnixEpoch.AddDays(-daysAgo),
        MachineType: machine,
        Target: "cloud",
        StartedAt: DateTimeOffset.UnixEpoch.AddDays(-daysAgo),
        FinishedAt: finished ? DateTimeOffset.UnixEpoch.AddDays(-daysAgo).AddMinutes(minutes) : null);

    [Fact]
    public void A_zero_length_past_run_is_ignored_so_it_never_makes_a_free_estimate()
        => RunHistory.DurationsFor([Run(JobPhase.Completed, L4, 0, 1)], L4).ShouldBeEmpty();

    [Fact]
    public void The_history_selector_takes_only_completed_runs_of_the_same_machine_newest_first_and_at_most_ten()
    {
        var runs = new List<RunRecord>
        {
            Run(JobPhase.Completed, L4, 7, 1),
            Run(JobPhase.Failed, L4, 99, 1),
            Run(JobPhase.Completed, A100, 99, 1),
            Run(JobPhase.Completed, L4, 99, 1, finished: false),
            Run(JobPhase.Cancelled, L4, 99, 1),
            Run(JobPhase.Completed, L4, 6, 2),
        };
        runs.AddRange(Enumerable.Range(3, 12).Select(d => Run(JobPhase.Completed, L4, 1, d)));

        var durations = RunHistory.DurationsFor(runs, L4);

        durations.Count.ShouldBe(10);
        durations[0].ShouldBe(TimeSpan.FromMinutes(7), "newest first");
        durations[1].ShouldBe(TimeSpan.FromMinutes(6));
        durations.ShouldNotContain(TimeSpan.FromMinutes(99), "failed, cancelled, unfinished and other-machine runs say nothing about a good run");
    }
}
