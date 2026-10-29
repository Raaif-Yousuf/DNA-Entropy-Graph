using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issues #100 and #557: the one per-run cost figure (the run's spend-ledger entry) is what the Results header and the Runs row show.
/// Nothing is recomputed here, and a run with no entry shows nothing rather than $0.00.
/// </summary>
public sealed class RunCostTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public RunCostTextTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _strings.GetString(ResultsCopy.CostEstimate).Returns("est ${0}");
        _strings.GetString(ResultsCopy.CostEstimateSpot).Returns("est spot ${0}");
        _strings.GetString(ResultsCopy.CostNoCloud).Returns("no cloud cost");
        _strings.GetString(ResultsCopy.CostShort).Returns("about ${0}");
        _strings.GetString(ResultsCopy.CostShortSpot).Returns("about spot ${0}");
        _strings.GetString(ResultsCopy.CostShortNoCloud).Returns("none");
    }

    private static RunRecord Row(string target = "cloud", bool? spot = null)
        => new("r", JobPhase.Completed, Now, Target: target, IsSpot: spot);

    [Fact]
    public void The_Spot_label_follows_the_rate_the_ledger_used_not_only_the_flag_on_the_row()
    {
        var row = Row(spot: true) with { MachineType = "a3-highgpu-2g" };

        RunCostText.Header(row, 1.0, _strings, machine => false).ShouldBe("est $1.00", "no Spot price for the machine: the ledger charged the on-demand rate, so the line must not say Spot");
        RunCostText.Short(row, 1.0, _strings, machine => false).ShouldBe("about $1.00");
        RunCostText.Header(row, 1.0, _strings, machine => machine == "a3-highgpu-2g").ShouldBe("est spot $1.00");
        RunCostText.Short(row, 1.0, _strings, machine => machine == "a3-highgpu-2g").ShouldBe("about spot $1.00");
        RunCostText.Header(Row(spot: false), 1.0, _strings, machine => true).ShouldBe("est $1.00", "an on-demand run is never labelled Spot");
        RunCostText.Header(row, 1.0, _strings).ShouldBe("est spot $1.00", "no lookup given: the flag on the row is all there is");
    }

    [Fact]
    public void A_booked_figure_is_shown_to_the_cent()
    {
        RunCostText.Header(Row(), 0.4249, _strings).ShouldBe("est $0.42");
        RunCostText.Short(Row(), 12.005, _strings).ShouldBe("about $12.01");
    }

    [Fact]
    public void A_tiny_figure_shows_one_cent_never_zero()
    {
        RunCostText.Header(Row(), 0.0001, _strings).ShouldBe("est $0.01");
    }

    [Fact]
    public void A_spot_run_says_so()
    {
        RunCostText.Header(Row(spot: true), 1.0, _strings).ShouldBe("est spot $1.00");
        RunCostText.Short(Row(spot: true), 1.0, _strings).ShouldBe("about spot $1.00");
    }

    [Fact]
    public void A_run_on_this_PC_says_no_cloud_cost_even_if_a_figure_were_present()
    {
        RunCostText.Header(Row("local"), null, _strings).ShouldBe("no cloud cost");
        RunCostText.Short(Row("local"), 3.0, _strings).ShouldBe("none");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.5)]
    public void A_figure_that_is_not_a_real_amount_shows_no_line(double usd)
    {
        RunCostText.Header(Row(), usd, _strings).ShouldBeNull();
        RunCostText.Short(Row(), usd, _strings).ShouldBeNull();
    }

    [Fact]
    public void The_local_target_matches_whatever_its_case()
    {
        RunCostText.Header(Row(RunTargets.LocalRecord.ToUpperInvariant()), null, _strings).ShouldBe("no cloud cost");
        RunTargets.CloudRecord.ShouldBe("cloud");
        RunTargets.LocalRecord.ShouldBe("local");
    }

    [Fact]
    public void A_cloud_run_with_no_ledger_entry_shows_nothing()
    {
        RunCostText.Header(Row(), null, _strings).ShouldBeNull();
        RunCostText.Short(Row(), null, _strings).ShouldBeNull();
    }

    [Fact]
    public void A_figure_on_the_run_row_itself_is_never_read()
    {
        // Nothing writes RunRecord.EstimatedCostUsd any more; only the ledger entry counts, so a stray value there cannot show.
        var run = Row() with { EstimatedCostUsd = 5.0 };
        RunCostText.Header(run, null, _strings).ShouldBeNull();
        RunCostText.Short(run, null, _strings).ShouldBeNull();
    }

    [Fact]
    public void The_Runs_row_and_the_Results_header_carry_the_same_amount()
    {
        var run = Row(spot: false);
        RunCostText.Header(run, 7.777, _strings).ShouldEndWith("$7.78");
        RunCostText.Short(run, 7.777, _strings).ShouldEndWith("$7.78");
    }

    [Fact]
    public async Task A_source_that_throws_or_is_absent_reads_as_no_figures_but_the_caller_giving_up_propagates()
    {
        (await RunCostText.ReadBookedAsync(null, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        var failing = Substitute.For<IRunCostSource>();
        failing.GetBookedAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyDictionary<string, double>>>(_ => throw new IOException("db locked"));
        (await RunCostText.ReadBookedAsync(failing, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        var cancelling = Substitute.For<IRunCostSource>();
        cancelling.GetBookedAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyDictionary<string, double>>>(_ => throw new OperationCanceledException());
        await Should.ThrowAsync<OperationCanceledException>(() => RunCostText.ReadBookedAsync(cancelling, TestContext.Current.CancellationToken));
    }
}