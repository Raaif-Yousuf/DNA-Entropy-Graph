using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cost;

/// <summary>Issue #214: catalog prices with a daily cache, an as-of date the page can show, and the shipped list whenever the catalog cannot be used.</summary>
public sealed class CatalogPricingSourceTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly IPricingSource _shipped = Substitute.For<IPricingSource>();
    private readonly IPricingCatalogGateway _gateway = Substitute.For<IPricingCatalogGateway>();
    private readonly MemoryCache _cache = new();

    public CatalogPricingSourceTests()
    {
        _shipped.Load().Returns(PricingTable.Parse(PricingTableTests.Good));
        _gateway.ListComputeSkusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CatalogSku>>(CatalogFixtures.G2AndDisk()));
    }

    private CatalogPricingSource Source() => new(_shipped, _gateway, _cache, _clock);

    private void GatewayFails(Exception failure)
        => _gateway.ListComputeSkusAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<CatalogSku>>>(_ => throw failure);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemoryCache : IPricingCacheStore
    {
        public string? Json { get; set; }

        public int Writes { get; private set; }

        public string? Read() => Json;

        public void Write(string json)
        {
            Json = json;
            Writes++;
        }
    }

    private static CloudOperationException Failure(CloudErrorKind kind) => new(new CloudError(null, null, "scripted"), kind);

    [Fact]
    public void Before_any_refresh_it_serves_the_shipped_list_and_says_so()
    {
        var source = Source();

        source.Load().Table!.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(0.85);
        source.Current.Origin.ShouldBe(PricingOrigin.Shipped);
        source.Current.AsOf.ShouldBe(new DateOnly(2026, 9, 19));
    }

    [Fact]
    public async Task A_refresh_replaces_the_prices_stamps_today_and_writes_the_cache()
    {
        var source = Source();

        await source.RefreshIfStaleAsync(CancellationToken.None);

        source.Load().Table!.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
        source.Current.ShouldBe(new PricingSnapshot(PricingOrigin.Catalog, new DateOnly(2026, 10, 16), PricingFallbackReason.None));
        _cache.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_refresh_the_same_day_does_not_ask_Google_again()
    {
        var source = Source();
        await source.RefreshIfStaleAsync(CancellationToken.None);

        await source.RefreshIfStaleAsync(CancellationToken.None);

        await _gateway.Received(1).ListComputeSkusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cache_written_today_by_an_earlier_launch_is_used_without_asking_Google()
    {
        await Source().RefreshIfStaleAsync(CancellationToken.None);
        _gateway.ClearReceivedCalls();

        var next = Source();
        await next.RefreshIfStaleAsync(CancellationToken.None);

        await _gateway.DidNotReceive().ListComputeSkusAsync(Arg.Any<CancellationToken>());
        next.Current.Origin.ShouldBe(PricingOrigin.Cache);
        next.Load().Table!.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
    }

    [Fact]
    public async Task Tomorrow_the_cache_is_stale_and_one_more_read_refreshes_it()
    {
        await Source().RefreshIfStaleAsync(CancellationToken.None);
        _gateway.ClearReceivedCalls();
        _clock.Advance(TimeSpan.FromDays(1));

        var next = Source();
        await next.RefreshIfStaleAsync(CancellationToken.None);

        await _gateway.Received(1).ListComputeSkusAsync(Arg.Any<CancellationToken>());
        next.Current.AsOf.ShouldBe(new DateOnly(2026, 10, 17));
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission, PricingFallbackReason.PermissionDenied)]
    [InlineData(CloudErrorKind.ApiDisabled, PricingFallbackReason.ApiDisabled)]
    [InlineData(CloudErrorKind.Network, PricingFallbackReason.Network)]
    [InlineData(CloudErrorKind.Other, PricingFallbackReason.Other)]
    public async Task A_failed_read_falls_back_to_the_shipped_list_and_names_why(CloudErrorKind kind, PricingFallbackReason reason)
    {
        GatewayFails(Failure(kind));
        var source = Source();

        await source.RefreshIfStaleAsync(CancellationToken.None);

        source.Load().Table!.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(0.85);
        source.Current.ShouldBe(new PricingSnapshot(PricingOrigin.Shipped, new DateOnly(2026, 9, 19), reason));
        _cache.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task A_sign_in_that_has_gone_is_its_own_reason()
    {
        GatewayFails(new AccountAuthException(AuthErrorCodes.SigninExpired, "gone"));
        var source = Source();

        await source.RefreshIfStaleAsync(CancellationToken.None);

        source.Current.Reason.ShouldBe(PricingFallbackReason.SignedOut);
    }

    [Fact]
    public async Task A_catalog_that_prices_nothing_we_know_is_no_match_and_the_shipped_list_stays()
    {
        _gateway.ListComputeSkusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CatalogSku>>([]));
        var source = Source();

        await source.RefreshIfStaleAsync(CancellationToken.None);

        source.Current.Reason.ShouldBe(PricingFallbackReason.NoMatch);
        source.Current.Origin.ShouldBe(PricingOrigin.Shipped);
    }

    [Fact]
    public async Task A_stale_cache_beats_the_shipped_list_when_the_refresh_fails_and_is_labelled_stale()
    {
        await Source().RefreshIfStaleAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(3));
        GatewayFails(Failure(CloudErrorKind.Network));
        var next = Source();

        await next.RefreshIfStaleAsync(CancellationToken.None);

        next.Current.ShouldBe(new PricingSnapshot(PricingOrigin.CacheStale, new DateOnly(2026, 10, 16), PricingFallbackReason.Network));
        next.Load().Table!.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
    }

    [Fact]
    public async Task A_failed_refresh_is_not_retried_on_every_estimate_but_is_retried_an_hour_later()
    {
        GatewayFails(Failure(CloudErrorKind.Network));
        var source = Source();

        await source.RefreshIfStaleAsync(CancellationToken.None);
        await source.RefreshIfStaleAsync(CancellationToken.None);
        await _gateway.Received(1).ListComputeSkusAsync(Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromMinutes(61));
        await source.RefreshIfStaleAsync(CancellationToken.None);
        await _gateway.Received(2).ListComputeSkusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_corrupt_cache_is_ignored()
    {
        _cache.Json = "{ not json";
        var source = Source();

        await source.RefreshIfStaleAsync(CancellationToken.None);

        source.Current.Origin.ShouldBe(PricingOrigin.Catalog);
    }

    [Fact]
    public async Task Cancellation_is_not_a_fallback()
    {
        GatewayFails(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => Source().RefreshIfStaleAsync(new CancellationToken(true)));
    }

    [Fact]
    public void An_unreadable_shipped_list_with_no_cache_is_still_the_shipped_problem()
    {
        _shipped.Load().Returns(PricingLoadResult.Fail(PricingProblem.FileMissing, "gone"));

        Source().Load().Problem.ShouldBe(PricingProblem.FileMissing);
    }
}
