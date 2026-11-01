using System.Text;
using System.Xml.Linq;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #814: the line under the New run estimate says where the prices came from and as of when. One test per <see cref="PricingOrigin"/> and per
/// <see cref="PricingFallbackReason"/>, against the real <c>Resources.resw</c> text (so a key that is missing reads as the key, and fails here).
/// </summary>
public sealed class NewRunPriceSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 31);
    private static readonly DateOnly Yesterday = new(2026, 10, 30);
    private static readonly DateOnly Shipped = new(2026, 9, 1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-pricesrc-" + Guid.NewGuid().ToString("N"));
    private readonly ICostEstimateService _estimates = Substitute.For<ICostEstimateService>();
    private readonly ResourceStrings _strings = new();
    private readonly FakeTime _time = new();
    private readonly FakePricing _pricing = new(new PricingSnapshot(PricingOrigin.Shipped, Shipped, PricingFallbackReason.None));

    public NewRunPriceSourceTests()
    {
        Directory.CreateDirectory(_dir);
        EstimateReturns(CostEstimateResult.Of(new CostEstimate(9, 9, 0.13, 0.13, EstimateBasis.History, StoppedDiskUsdPerDay: 0.49)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = Now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakePricing(PricingSnapshot snapshot) : IPricingStatus
    {
        public PricingSnapshot Current { get; set; } = snapshot;
    }

    private sealed class ResourceStrings : IStringResourceProvider
    {
        private readonly Dictionary<string, string> _values = XDocument.Load(TestPaths.ReswPath).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

        public string GetString(string key) => _values.GetValueOrDefault(key, key);
    }

    private void EstimateReturns(CostEstimateResult result)
        => _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

    private async Task<NewRunViewModel> OpenWithAFileAsync(bool withPricing = true, IPricingStatus? pricing = null, ICostEstimateService? estimates = null, bool a100IsSpotOnlyAndPicked = false, GpuTier? spotOnlyTierPicked = null)
    {
        // The shipped catalog offers no Spot-only tier (H100 is not offered), so the Spot cases make one tier (the A100 by default) Spot-only in a test catalog.
        var tier = spotOnlyTierPicked ?? (a100IsSpotOnlyAndPicked ? GpuTier.A100_40 : (GpuTier?)null);
        var gpu = TestGpu.Plan(tier is { } t ? SpotOnlyCatalog(t) : null);
        if (tier is { } picked)
        {
            gpu.SelectedGpu = gpu.Gpus.Single(g => g.Tier == picked);
        }

        var vm = new NewRunViewModel(
            Substitute.For<IFilePicker>(),
            Substitute.For<IJobEngine>(),
            Substitute.For<INavigator>(),
            _strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
            estimates ?? _estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()),
            NullDiagnosticsLog.Instance,
            gpu,
            clock: _time,
            pricing: withPricing ? pricing ?? _pricing : null);
        var path = Path.Combine(_dir, "a.fasta");
        await File.WriteAllTextAsync(path, ">a\n" + string.Concat(Enumerable.Repeat("ACGT", 40)) + "\n", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await vm.AddDroppedCommand.ExecuteAsync(new DroppedItems([path], 0, false));
        return vm;
    }

    private static async Task<string> LineAsync(NewRunViewModel vm)
    {
        for (var i = 0; i < 500 && vm.EstimateText.Length == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        vm.EstimateText.ShouldNotBeEmpty("the estimate line has to be there for the price line to sit under it");
        return vm.PriceSourceText;
    }

    private async Task<string> LineForAsync(PricingOrigin origin, DateOnly asOf, PricingFallbackReason reason = PricingFallbackReason.None)
    {
        _pricing.Current = new PricingSnapshot(origin, asOf, reason);
        return await LineAsync(await OpenWithAFileAsync());
    }

    [Theory]
    [InlineData(PricingOrigin.Catalog)]
    [InlineData(PricingOrigin.Cache)]
    public async Task Catalog_prices_read_today_say_so_with_todays_date_and_no_reason(PricingOrigin origin)
    {
        var line = await LineForAsync(origin, Today);

        line.ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
    }

    [Fact]
    public async Task Cache_stale_prices_read_as_older_prices_with_their_own_date_never_as_todays()
    {
        var line = await LineForAsync(PricingOrigin.CacheStale, Yesterday);

        line.ShouldContain("30 Oct 2026");
        line.ShouldContain("older prices");
        line.ShouldNotContain("31 Oct 2026");
        line.ShouldNotBe("Prices from Google's price list, as of 30 Oct 2026.", "an old read must not read like a fresh one");
    }

    [Fact]
    public async Task A_session_that_crosses_midnight_reads_as_older_prices_through_the_real_catalog_source_mapping()
    {
        // The real CatalogPricingSource (not a fake status) maps a day-old Catalog read to CacheStale in Current; the line must follow that.
        var shipped = Substitute.For<IPricingSource>();
        shipped.Load().Returns(PricingTable.Parse(ShippedJson));
        var gateway = Substitute.For<DnaEntropyGraph.Core.Cloud.IPricingCatalogGateway>();
        gateway.ListComputeSkusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DnaEntropyGraph.Core.Cloud.CatalogSku>>(G2Skus()));
        var source = new CatalogPricingSource(shipped, gateway, new MemoryCache(), _time);
        await source.RefreshIfStaleAsync(TestContext.Current.CancellationToken);
        source.Current.Origin.ShouldBe(PricingOrigin.Catalog, "the read worked today");
        _time.Advance(TimeSpan.FromDays(1));
        source.Current.Origin.ShouldBe(PricingOrigin.CacheStale, "the real source ages it at midnight");

        var vm = await OpenWithAFileAsync(pricing: source);
        var line = await LineAsync(vm);

        line.ShouldContain("older prices");
        line.ShouldContain("31 Oct 2026");
        line.ShouldNotContain("1 Nov 2026");
    }

    [Fact]
    public async Task A_catalog_date_one_day_ahead_of_this_clock_never_reads_as_a_date_in_the_future()
    {
        // CatalogPricingSource accepts a cache dated up to a day ahead (a time zone or a clock a little fast); the line says today instead.
        var line = await LineForAsync(PricingOrigin.Cache, Today.AddDays(1));

        line.ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
    }

    [Fact]
    public async Task Older_prices_with_no_date_do_not_read_as_of_nothing()
    {
        var line = await LineForAsync(PricingOrigin.CacheStale, default);

        line.ShouldContain("older prices");
        line.ShouldNotContain("as of .");
        line.ShouldNotContain("as of  ");
        line.ShouldNotContain("PriceSource_", Case.Sensitive);
    }

    [Fact]
    public async Task The_shipped_list_with_no_failure_says_where_it_is_from_and_its_date_and_gives_no_reason()
    {
        var line = await LineForAsync(PricingOrigin.Shipped, Shipped);

        line.ShouldBe("Prices from the list that came with this app, as of 1 Sep 2026.");
    }

    // Issue #1074. The A100 is Spot-only (machine a2-highgpu-1g); the default L4 (g2-standard-8) is on-demand.
    private const string SpotNote = "The discounted (Spot) price is from the list that came with this app, not from Google's price list.";

    private static GpuCatalog SpotOnlyCatalog(GpuTier tier)
        => new(GpuCatalog.Shipped.Models, [.. GpuCatalog.Shipped.Gpus.Select(g => g.Tier == tier ? g with { SpotOnly = true } : g)]);

    private const string ShippedWithSpotJson = """
        {
          "schema": 1,
          "asOf": "2026-09-01",
          "currency": "USD",
          "region": "us-central1",
          "source": "test",
          "disk": { "type": "pd-balanced", "usdPerGbMonth": 0.10 },
          "machines": {
            "g2-standard-8": { "accelerator": "1x NVIDIA L4", "onDemandUsdPerHour": 0.85, "spotUsdPerHour": 0.18 },
            "a2-highgpu-1g": { "accelerator": "1x NVIDIA A100 40GB", "onDemandUsdPerHour": 3.67, "spotUsdPerHour": 1.10 }
          }
        }
        """;

    /// <summary>The real resolver and source (not a hand-made snapshot) over the catalog SKUs, then the page.</summary>
    private async Task<string> LineThroughRealResolverAsync(GpuTier spotTier, IReadOnlyList<DnaEntropyGraph.Core.Cloud.CatalogSku> skus)
    {
        var shipped = Substitute.For<IPricingSource>();
        shipped.Load().Returns(PricingTable.Parse(ShippedWithSpotJson));
        var gateway = Substitute.For<DnaEntropyGraph.Core.Cloud.IPricingCatalogGateway>();
        gateway.ListComputeSkusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(skus));
        var source = new CatalogPricingSource(shipped, gateway, new MemoryCache(), _time);
        await source.RefreshIfStaleAsync(TestContext.Current.CancellationToken);
        source.Current.Origin.ShouldBe(PricingOrigin.Catalog);
        var vm = await OpenWithAFileAsync(pricing: source, spotOnlyTierPicked: spotTier);
        vm.Gpu.RequiresSpot.ShouldBeTrue();
        return await LineAsync(vm);
    }

    [Fact]
    public async Task A_Spot_machine_Google_gave_no_price_for_at_all_says_its_shipped_Spot_price_is_on_screen()
    {
        // The catalog prices only the L4; the A100 row (Spot price included) is copied whole from the shipped list.
        var line = await LineThroughRealResolverAsync(GpuTier.A100_40, G2Skus());

        line.ShouldContain(SpotNote);
    }

    [Fact]
    public async Task A_Spot_price_dropped_for_being_above_on_demand_is_not_named_as_a_shipped_Spot_price()
    {
        // Every on-demand SKU is scaled down so the shipped 0.18 Spot price is above it and is discarded: no Spot price is on screen, so no note.
        var cheap = G2Skus().Select(s => s with { UsdPerUnit = s.Description.StartsWith("Balanced", StringComparison.Ordinal) ? s.UsdPerUnit : s.UsdPerUnit * 0.01 }).ToArray();

        var line = await LineThroughRealResolverAsync(GpuTier.L4, cheap);

        // ShouldBe, not ShouldNotContain: an empty line (no estimate) would pass a negative check vacuously. The exact catalog line
        // proves an estimate was on screen. The Core test CatalogPriceResolverTests.A_kept_Spot_price_dropped_for_being_above_on_demand_is_not_listed_as_kept
        // guards the resolver itself.
        line.ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
    }

    [Fact]
    public async Task A_Spot_price_that_survives_from_the_shipped_list_beside_a_Google_on_demand_price_is_named()
    {
        (await LineThroughRealResolverAsync(GpuTier.L4, G2Skus())).ShouldContain(SpotNote);
    }

    private async Task<string> SpotLineAsync(PricingOrigin origin, string[]? keptFromShipped, bool pickA100 = true)
    {
        _pricing.Current = new PricingSnapshot(origin, Today, PricingFallbackReason.None, keptFromShipped);
        var vm = await OpenWithAFileAsync(a100IsSpotOnlyAndPicked: pickA100);
        vm.Gpu.RequiresSpot.ShouldBe(pickA100, "the test must be pricing the machine it thinks it is");
        return await LineAsync(vm);
    }

    [Fact]
    public async Task A_Spot_estimate_whose_Spot_price_came_from_the_shipped_list_says_so_after_the_catalog_line()
    {
        var line = await SpotLineAsync(PricingOrigin.Catalog, ["a2-highgpu-1g"]);

        line.ShouldBe("Prices from Google's price list, as of 31 Oct 2026. " + SpotNote);
        line.ShouldNotContain("—", Case.Sensitive);
    }

    [Fact]
    public async Task A_Spot_estimate_with_Google_Spot_prices_adds_nothing()
    {
        (await SpotLineAsync(PricingOrigin.Catalog, null)).ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
        (await SpotLineAsync(PricingOrigin.Catalog, [])).ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
    }

    [Fact]
    public async Task An_on_demand_estimate_never_shows_the_Spot_note_even_when_a_Spot_price_was_kept()
    {
        var line = await SpotLineAsync(PricingOrigin.Catalog, ["g2-standard-8", "a2-highgpu-1g"], pickA100: false);

        line.ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
    }

    [Fact]
    public async Task A_Spot_estimate_for_a_machine_whose_Spot_price_is_Googles_does_not_show_the_note_for_another_machine()
    {
        (await SpotLineAsync(PricingOrigin.Catalog, ["g2-standard-8"])).ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
    }

    [Fact]
    public async Task When_everything_is_already_from_the_shipped_list_the_Spot_note_is_not_repeated()
    {
        (await SpotLineAsync(PricingOrigin.Shipped, ["a2-highgpu-1g"])).ShouldBe("Prices from the list that came with this app, as of 31 Oct 2026.");
    }

    [Theory]
    [InlineData(PricingFallbackReason.SignedOut)]
    [InlineData(PricingFallbackReason.Network)]
    public async Task The_Spot_note_comes_before_a_failure_reason_so_the_action_stays_last(PricingFallbackReason reason)
    {
        _pricing.Current = new PricingSnapshot(PricingOrigin.CacheStale, Today, reason, ["a2-highgpu-1g"]);
        var vm = await OpenWithAFileAsync(a100IsSpotOnlyAndPicked: true);

        var line = await LineAsync(vm);

        line.ShouldContain(SpotNote);
        line.IndexOf(SpotNote, StringComparison.Ordinal).ShouldBeLessThan(line.IndexOf(reason == PricingFallbackReason.SignedOut ? "signed out" : "connection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PricingFallbackReason.SignedOut, "signed out", "Sign in again")]
    [InlineData(PricingFallbackReason.PermissionDenied, "not allowed", "Ask the project owner")]
    [InlineData(PricingFallbackReason.ApiDisabled, "price list service", "Ask the project owner to switch it on")]
    [InlineData(PricingFallbackReason.Network, "connection", "Check your internet connection")]
    [InlineData(PricingFallbackReason.Other, "could not be read this time", "Try again later")]
    [InlineData(PricingFallbackReason.NoMatch, "did not cover", "Try again later")]
    public async Task The_shipped_list_after_a_failed_read_gives_its_date_the_reason_in_plain_words_and_one_action(PricingFallbackReason reason, string because, string action)
    {
        var line = await LineForAsync(PricingOrigin.Shipped, Shipped, reason);

        line.ShouldStartWith("Prices from the list that came with this app, as of 1 Sep 2026.");
        line.ShouldContain(because);
        line.ShouldContain(action);
        line.ShouldNotContain("—", Case.Sensitive);
    }

    [Fact]
    public async Task Each_failure_reason_reads_differently()
    {
        var lines = new List<string>();
        foreach (var reason in new[] { PricingFallbackReason.SignedOut, PricingFallbackReason.PermissionDenied, PricingFallbackReason.ApiDisabled, PricingFallbackReason.Network, PricingFallbackReason.Other, PricingFallbackReason.NoMatch })
        {
            lines.Add(await LineForAsync(PricingOrigin.Shipped, Shipped, reason));
        }

        lines.Distinct().Count().ShouldBe(lines.Count);
    }

    [Fact]
    public async Task The_switched_off_service_reason_leads_with_the_plain_phrase_and_gives_exactly_one_action()
    {
        var line = await LineForAsync(PricingOrigin.Shipped, Shipped, PricingFallbackReason.ApiDisabled);

        line.IndexOf("price list service", StringComparison.Ordinal).ShouldBeGreaterThan(-1);
        line.IndexOf("price list service", StringComparison.Ordinal).ShouldBeLessThan(line.IndexOf("Cloud Billing API", StringComparison.Ordinal), "the plain phrase comes before the product name");
        line.ShouldNotContain("yourself");
        line.ShouldNotContain("console");
        System.Text.RegularExpressions.Regex.Matches(line, "Ask |Sign in|Check |Try ").Count.ShouldBe(1, "one action only");
    }

    [Fact]
    public async Task Stale_catalog_prices_after_a_failed_read_give_the_older_prices_line_and_the_reason()
    {
        var line = await LineForAsync(PricingOrigin.CacheStale, Yesterday, PricingFallbackReason.Network);

        line.ShouldContain("older prices");
        line.ShouldContain("30 Oct 2026");
        line.ShouldContain("connection");
    }

    [Fact]
    public async Task With_no_estimate_there_is_no_price_line()
    {
        // The real estimate is held at the gate. The cap card is written in the same step as the estimate and price lines, last, and the substitute
        // gives a cap only after the gate opened, so a card that shows can only have come from the real estimate's step (the no-file step asks
        // earlier and gets none): once it shows, that step has run and had its chance to write a price line.
        var gate = new TaskCompletionSource<CostEstimateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = false;
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(gate.Task);
        _estimates.CheckSpendCapAsync(Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<SpendCapStatus?>(Volatile.Read(ref opened) ? new SpendCapStatus(SpendCapLevel.Warning, 8.0, 10.0) : null));
        var vm = await OpenWithAFileAsync();
        vm.SpendCapText.ShouldBeEmpty("the real estimate has not answered yet");

        Volatile.Write(ref opened, true);
        gate.SetResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize));
        await WaitForAsync(vm, nameof(NewRunViewModel.SpendCapText), () => vm.SpendCapText.Length > 0);

        vm.EstimateText.ShouldBeEmpty();
        vm.PriceSourceText.ShouldBeEmpty();
        vm.HasPriceSource.ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_that_finishes_after_the_estimate_stopped_waiting_and_fails_adds_the_reason_without_another_estimate()
    {
        // Issue #814 review round 2 (M1): the real estimate service, a source whose read outlives the estimate's wait and then fails. The page
        // must be told by the service, not by the user asking again.
        var source = new LateFailingSource();
        var service = new CostEstimateService(source, Substitute.For<IRunRepository>(), TimeSpan.FromMilliseconds(20));
        var vm = await OpenWithAFileAsync(estimates: service, pricing: source);
        await WaitForAsync(vm, nameof(NewRunViewModel.PriceSourceText), () => vm.PriceSourceText.Length > 0);
        vm.PriceSourceText.ShouldBe("Prices from the list that came with this app, as of 1 Sep 2026.", "the read had not finished when the estimate answered");
        source.Refreshes.ShouldBe(1);

        source.FinishWithNetworkFailure();
        await WaitForAsync(vm, nameof(NewRunViewModel.PriceSourceText), () => vm.PriceSourceText.Contains("connection", StringComparison.Ordinal));

        vm.PriceSourceText.ShouldContain("Check your internet connection");
    }

    private static async Task WaitForAsync(NewRunViewModel vm, string property, Func<bool> done)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == property && done())
            {
                tcs.TrySetResult();
            }
        }

        vm.PropertyChanged += Handler;
        try
        {
            if (!done())
            {
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            vm.PropertyChanged -= Handler;
        }
    }

    /// <summary>Serves the shipped list; its read waits to be released and then fails with a network problem, leaving the prices as they were.</summary>
    private sealed class LateFailingSource : IPricingSource, IPricingRefresh, IPricingStatus
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PricingSnapshot _current = new(PricingOrigin.Shipped, Shipped, PricingFallbackReason.None);
        private int _refreshes;

        public int Refreshes => Volatile.Read(ref _refreshes);

        public PricingSnapshot Current => Volatile.Read(ref _current);

        public void FinishWithNetworkFailure() => _release.TrySetResult();

        public PricingLoadResult Load() => PricingTable.Parse(ShippedJson);

        public async Task RefreshIfStaleAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _refreshes) > 1)
            {
                return;
            }

            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, _current with { Reason = PricingFallbackReason.Network });
        }
    }

    private sealed class MemoryCache : IPricingCacheStore
    {
        private string? _json;

        public string? Read() => _json;

        public void Write(string json) => _json = json;
    }

    private const string ShippedJson = """
        {
          "schema": 1,
          "asOf": "2026-09-01",
          "currency": "USD",
          "region": "us-central1",
          "source": "test",
          "disk": { "type": "pd-balanced", "usdPerGbMonth": 0.10 },
          "machines": {
            "g2-standard-8": { "accelerator": "1x NVIDIA L4", "onDemandUsdPerHour": 0.85, "spotUsdPerHour": 0.18 },
            "a2-highgpu-1g": { "accelerator": "1x NVIDIA A100 40GB", "onDemandUsdPerHour": 3.67, "spotUsdPerHour": null }
          }
        }
        """;

    private static DnaEntropyGraph.Core.Cloud.CatalogSku Sku(string description, string family, string usage, double price, string unit = "h")
        => new("sku-" + description, description, family, "x", usage, ["us-central1"], price, unit);

    private static DnaEntropyGraph.Core.Cloud.CatalogSku[] G2Skus() =>
    [
        Sku("G2 Instance Core running in Americas", "Compute", "OnDemand", 0.1),
        Sku("G2 Instance Ram running in Americas", "Compute", "OnDemand", 0.01, "GBy.h"),
        Sku("Nvidia L4 GPU running in Americas", "Compute", "OnDemand", 1.0),
        Sku("Balanced PD Capacity", "Storage", "OnDemand", 0.2, "GBy.mo"),
    ];

    [Fact]
    public async Task A_page_with_no_pricing_status_shows_no_price_line()
    {
        var vm = await OpenWithAFileAsync(withPricing: false);
        await LineAsync(vm);

        vm.PriceSourceText.ShouldBeEmpty();
        vm.HasPriceSource.ShouldBeFalse();
    }

    [Fact]
    public async Task The_line_follows_the_status_when_the_estimate_is_made_again()
    {
        _pricing.Current = new PricingSnapshot(PricingOrigin.Shipped, Shipped, PricingFallbackReason.Network);
        var vm = await OpenWithAFileAsync();
        (await LineAsync(vm)).ShouldContain("connection");

        _pricing.Current = new PricingSnapshot(PricingOrigin.Catalog, Today, PricingFallbackReason.None);
        vm.ReceiveNavigationParameterCommand.Execute(null);
        for (var i = 0; i < 500 && vm.PriceSourceText.Contains("connection", StringComparison.Ordinal); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        vm.PriceSourceText.ShouldBe("Prices from Google's price list, as of 31 Oct 2026.");
        vm.HasPriceSource.ShouldBeTrue();
    }
}
