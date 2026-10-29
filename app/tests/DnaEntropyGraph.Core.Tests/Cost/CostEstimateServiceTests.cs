using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cost;

/// <summary>Issue #98: the service the New run page asks. It joins the price list, the run history and the arithmetic.</summary>
public sealed class CostEstimateServiceTests
{
    private const string L4 = "g2-standard-8";

    private readonly IRunRepository _runs = Substitute.For<IRunRepository>();
    private readonly IPricingSource _pricing = Substitute.For<IPricingSource>();

    public CostEstimateServiceTests()
    {
        _pricing.Load().Returns(PricingTable.Parse(PricingTableTests.Good));
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>([]));
    }

    private CostEstimateService Service() => new(_pricing, _runs);

    [Fact]
    public async Task With_no_past_runs_it_estimates_from_the_model_for_the_given_size()
    {
        var result = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        result.Estimate!.Basis.ShouldBe(EstimateBasis.Model);
    }

    [Fact]
    public async Task Past_completed_runs_of_the_machine_switch_it_to_history()
    {
        var start = DateTimeOffset.UnixEpoch;
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>(
            [new RunRecord("a", JobPhase.Completed, start, MachineType: L4, StartedAt: start, FinishedAt: start.AddMinutes(9))]));

        var result = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        result.Estimate!.Basis.ShouldBe(EstimateBasis.History);
        result.Estimate.MinMinutes.ShouldBe(9 + (5_000 / 1000.0 * 2.0 / 60), 1e-9);
    }

    [Fact]
    public async Task The_runs_own_boot_disk_size_reaches_the_estimate()
    {
        var small = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        var large = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 300, CancellationToken.None);

        large.Estimate!.StoppedDiskUsdPerDay.ShouldBe(small.Estimate!.StoppedDiskUsdPerDay * 2, 1e-9);
    }

    [Fact]
    public async Task A_price_list_that_could_not_be_read_is_unavailable_with_that_reason()
    {
        _pricing.Load().Returns(new PricingLoadResult(null, PricingProblem.FileMissing, "gone"));

        var result = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        result.Estimate.ShouldBeNull();
        result.Reason.ShouldBe(EstimateUnavailable.PriceListUnreadable);
    }

    [Fact]
    public async Task A_history_that_cannot_be_read_falls_back_to_the_model_instead_of_failing_the_page()
    {
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<RunRecord>>>(_ => throw new InvalidOperationException("db locked"));

        var result = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        result.Estimate!.Basis.ShouldBe(EstimateBasis.Model);
    }

    [Fact]
    public async Task The_price_list_is_read_once_not_on_every_estimate()
    {
        var service = Service();

        await service.EstimateAsync(L4, false, 1_000, 150, CancellationToken.None);
        await service.EstimateAsync(L4, false, 2_000, 150, CancellationToken.None);

        _pricing.Received(1).Load();
    }

    [Fact]
    public async Task A_cancelled_call_throws_cancellation_not_a_fallback()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<RunRecord>>>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => Service().EstimateAsync(L4, false, 1_000, 150, cts.Token));
    }

    private sealed class RefreshingSource : IPricingSource, IPricingRefresh
    {
        public double OnDemand { get; private set; } = 0.85;

        public int Refreshes { get; private set; }

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good.Replace("0.85", OnDemand.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            OnDemand = 1.7;
            return Task.CompletedTask;
        }
    }

    private sealed class SlowSource : IPricingSource, IPricingRefresh
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double _onDemand = 0.85;

        public int Refreshes { get; private set; }

        public void Finish()
        {
            _onDemand = 1.7;
            _release.SetResult();
        }

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good.Replace("0.85", _onDemand.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public async Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            await _release.Task.ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task A_slow_refresh_does_not_hold_the_estimate_and_the_next_estimate_uses_what_it_found()
    {
        // Issue #214 review M1: the page waits for an estimate; a catalog read that takes a minute must not.
        var source = new SlowSource();
        var service = new CostEstimateService(source, _runs, TimeSpan.FromMilliseconds(50));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        var again = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        source.Finish();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var later = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        var shipped = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        first.Estimate!.MaxUsd.ShouldBe(shipped.Estimate!.MaxUsd, "the first estimate is made from what is known now");
        again.Estimate!.MaxUsd.ShouldBe(first.Estimate.MaxUsd);
        source.Refreshes.ShouldBe(2, "a refresh still running is not started a second time; the one after it finished is");
        later.Estimate!.MaxUsd.ShouldBeGreaterThan(first.Estimate.MaxUsd, "the refresh that finished in the background reaches the next estimate");
    }

    [Fact]
    public async Task A_source_that_can_refresh_is_refreshed_first_and_read_again_so_a_new_price_reaches_the_estimate()
    {
        var source = new RefreshingSource();
        var service = new CostEstimateService(source, _runs);

        var first = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        var second = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        source.Refreshes.ShouldBe(2);
        var shipped = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        first.Estimate!.MaxUsd.ShouldBeGreaterThan(shipped.Estimate!.MaxUsd, "the refreshed price must be the one used, on the very first estimate");
        second.Estimate!.MaxUsd.ShouldBe(first.Estimate.MaxUsd);
    }
}
