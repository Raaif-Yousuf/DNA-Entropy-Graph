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
        private int _refreshes;
        private double _onDemand = 0.85;

        public double OnDemand => Volatile.Read(ref _onDemand);

        public int Refreshes => Volatile.Read(ref _refreshes);

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good.Replace("0.85", OnDemand.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshes);
            Volatile.Write(ref _onDemand, 1.7);
            return Task.CompletedTask;
        }
    }

    /// <summary>A refresh that waits to be released and only THEN moves the price it serves, so a price seen by an estimate can only have come through the service reading the source again.</summary>
    private sealed class SlowSource : IPricingSource, IPricingRefresh
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double _onDemand = 0.85;
        private int _refreshes;

        public int Refreshes => Volatile.Read(ref _refreshes);

        /// <summary>Set inside the source, the moment a refresh has begun (on whatever thread it ran on).</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Finish() => _release.SetResult();

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good.Replace("0.85", Volatile.Read(ref _onDemand).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public async Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshes);
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            Volatile.Write(ref _onDemand, 1.7);
        }
    }

    /// <summary>A refresh that never ends on its own; it hands out the token it was given.</summary>
    private sealed class HangingSource : IPricingSource, IPricingRefresh
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good);

        public Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(cancellationToken);
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    [Fact]
    public async Task A_slow_refresh_does_not_hold_the_estimate_and_the_next_estimate_uses_what_it_found()
    {
        // Issue #214 review M1: the page waits for an estimate; a catalog read that takes a minute must not.
        var source = new SlowSource();
        var service = new CostEstimateService(source, _runs, TimeSpan.FromMilliseconds(50));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PricesChanged += () => changed.TrySetResult();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        var again = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        source.Refreshes.ShouldBe(1, "a refresh still running is not started a second time");

        source.Finish();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var later = await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        var shipped = await Service().EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        first.Estimate!.MaxUsd.ShouldBe(shipped.Estimate!.MaxUsd, "the first estimate is made from what is known now");
        again.Estimate!.MaxUsd.ShouldBe(first.Estimate.MaxUsd);
        later.Estimate!.MaxUsd.ShouldBeGreaterThan(first.Estimate.MaxUsd, "the refresh that finished in the background reaches the next estimate, through the service reading the source again");
    }

    [Fact]
    public async Task A_background_refresh_that_changed_nothing_raises_no_prices_changed()
    {
        var source = new UnchangedAfterRefresh();
        var service = new CostEstimateService(source, _runs, TimeSpan.FromSeconds(30));
        var raised = 0;
        service.PricesChanged += () => Interlocked.Increment(ref raised);

        // The estimate waits for the refresh it started, and the service decides about PricesChanged inside that same task, so when this returns the decision is made.
        await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        source.Refreshes.ShouldBe(1, "the refresh did run, so a zero below is a decision and not a refresh that never happened");
        Volatile.Read(ref raised).ShouldBe(0, "the refresh left the prices as they were, so there is nothing for the page to redo");
    }

    [Fact]
    public async Task A_background_refresh_that_changed_the_prices_raises_prices_changed_once()
    {
        // The positive control of the test above: same waiting, a source whose refresh does move a price.
        var source = new RefreshingSource();
        var service = new CostEstimateService(source, _runs, TimeSpan.FromSeconds(30));
        var raised = 0;
        service.PricesChanged += () => Interlocked.Increment(ref raised);

        await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        Volatile.Read(ref raised).ShouldBe(1);
    }

    [Fact]
    public async Task A_refresh_with_the_same_prices_on_a_new_day_raises_prices_changed_because_the_line_under_the_estimate_changes()
    {
        // Issue #214 review round 4 (L): the fingerprint used to be the table's whole JSON, so a new day alone looked like a price change.
        // Issue #814 review round 2 (M1): the price source line (older prices -> as of today) is made from the status, so a status change IS something the page must redo.
        var clock = new DayClock(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));
        var shipped = Substitute.For<IPricingSource>();
        shipped.Load().Returns(PricingTable.Parse(PricingTableTests.Good));
        var gateway = Substitute.For<DnaEntropyGraph.Core.Cloud.IPricingCatalogGateway>();
        gateway.ListComputeSkusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DnaEntropyGraph.Core.Cloud.CatalogSku>>(CatalogFixtures.G2AndDisk()));
        var cache = new OneFileCache();
        await new CatalogPricingSource(shipped, gateway, cache, clock).RefreshIfStaleAsync(CancellationToken.None);
        cache.Json.ShouldNotBeNull("day one wrote the cache");

        clock.Advance(TimeSpan.FromDays(1));
        var source = new CatalogPricingSource(shipped, gateway, cache, clock);
        source.Current.Origin.ShouldBe(PricingOrigin.CacheStale);
        var service = new CostEstimateService(source, _runs, TimeSpan.FromSeconds(30));
        var raised = 0;
        service.PricesChanged += () => Interlocked.Increment(ref raised);

        await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        source.Current.AsOf.ShouldBe(new DateOnly(2026, 10, 17), "the catalog was read again on the new day");
        source.Current.Origin.ShouldBe(PricingOrigin.Catalog);
        await gateway.Received(2).ListComputeSkusAsync(Arg.Any<CancellationToken>());
        Volatile.Read(ref raised).ShouldBe(1, "the prices are the same but the status went from older prices to today's, so the line on screen is out of date");
    }

    [Fact]
    public async Task A_refresh_that_fails_with_the_prices_unchanged_raises_prices_changed_so_the_line_can_name_the_reason()
    {
        var source = new FailingStatusSource();
        var service = new CostEstimateService(source, _runs, TimeSpan.FromSeconds(30));
        var raised = 0;
        service.PricesChanged += () => Interlocked.Increment(ref raised);

        await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        source.Current.Reason.ShouldBe(PricingFallbackReason.Network);
        Volatile.Read(ref raised).ShouldBe(1);
    }

    [Fact]
    public async Task A_refresh_that_leaves_prices_and_status_alone_raises_nothing_even_for_a_source_with_a_status()
    {
        var source = new FailingStatusSource { FailsOnRefresh = false };
        var service = new CostEstimateService(source, _runs, TimeSpan.FromSeconds(30));
        var raised = 0;
        service.PricesChanged += () => Interlocked.Increment(ref raised);

        await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);

        source.Refreshes.ShouldBe(1);
        Volatile.Read(ref raised).ShouldBe(0);
    }

    /// <summary>Serves the same prices always; its refresh (when it fails) only changes the status.</summary>
    private sealed class FailingStatusSource : IPricingSource, IPricingRefresh, IPricingStatus
    {
        private int _refreshes;
        private PricingSnapshot _current = new(PricingOrigin.Shipped, new DateOnly(2026, 9, 19), PricingFallbackReason.None);

        public bool FailsOnRefresh { get; init; } = true;

        public int Refreshes => Volatile.Read(ref _refreshes);

        public PricingSnapshot Current => Volatile.Read(ref _current);

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good);

        public Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshes);
            if (FailsOnRefresh)
            {
                Volatile.Write(ref _current, _current with { Reason = PricingFallbackReason.Network });
            }

            return Task.CompletedTask;
        }
    }

    private sealed class DayClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class OneFileCache : IPricingCacheStore
    {
        public string? Json { get; private set; }

        public string? Read() => Json;

        public void Write(string json) => Json = json;
    }

    /// <summary>Takes the refresh but always serves the shipped price.</summary>
    private sealed class UnchangedAfterRefresh : IPricingSource, IPricingRefresh
    {
        private int _refreshes;

        public int Refreshes => Volatile.Read(ref _refreshes);

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good);

        public Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshes);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Disposing_the_service_cancels_a_refresh_that_is_still_running()
    {
        // Issue #214 review round 2 (M): the background read used CancellationToken.None and was owned by nobody.
        var source = new HangingSource();
        var service = new CostEstimateService(source, _runs, TimeSpan.FromMilliseconds(20));
        await service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None);
        var token = await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        token.IsCancellationRequested.ShouldBeFalse("a running read is not cancelled by the estimate that stopped waiting for it");

        ((IDisposable)service).Dispose();

        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task The_refresh_does_not_start_on_the_callers_thread()
    {
        // Issue #214 review round 2 (L): the source reads files first; on the UI thread that is a stall.
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ThreadRecordingSource(started);
        var service = new CostEstimateService(source, _runs, TimeSpan.FromMilliseconds(20));
        var callerThread = 0;

        // A thread of its own (not a pool thread that the refresh could land on by chance) stands for the UI thread.
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            service.EstimateAsync(L4, spot: false, bases: 5_000, bootDiskGb: 150, CancellationToken.None).GetAwaiter().GetResult();
        });
        caller.Start();
        caller.Join();

        (await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldNotBe(callerThread);
        source.Complete();
    }

    private sealed class ThreadRecordingSource(TaskCompletionSource<int> started) : IPricingSource, IPricingRefresh
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _done.TrySetResult();

        public PricingLoadResult Load() => PricingTable.Parse(PricingTableTests.Good);

        public Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            started.TrySetResult(Environment.CurrentManagedThreadId);
            return _done.Task;
        }
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
