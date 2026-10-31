using System.Text;
using System.Xml.Linq;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
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
        public override DateTimeOffset GetUtcNow() => Now;
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

    private async Task<NewRunViewModel> OpenWithAFileAsync(bool withPricing = true)
    {
        var vm = new NewRunViewModel(
            Substitute.For<IFilePicker>(),
            Substitute.For<IJobEngine>(),
            Substitute.For<INavigator>(),
            _strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
            _estimates,
            new OutputSelectionStore(Substitute.For<ISettingsStore>()),
            NullDiagnosticsLog.Instance,
            TestGpu.Plan(),
            clock: new FakeTime(),
            pricing: withPricing ? _pricing : null);
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
    public async Task A_catalog_read_that_is_a_day_old_reads_as_older_prices_even_though_its_origin_is_catalog()
    {
        // A session that crosses midnight: the origin stays Catalog while the date goes stale.
        var line = await LineForAsync(PricingOrigin.Catalog, Yesterday);

        line.ShouldContain("older prices");
        line.ShouldContain("30 Oct 2026");
    }

    [Fact]
    public async Task The_shipped_list_with_no_failure_says_where_it_is_from_and_its_date_and_gives_no_reason()
    {
        var line = await LineForAsync(PricingOrigin.Shipped, Shipped);

        line.ShouldBe("Prices from the list that came with this app, as of 1 Sep 2026.");
    }

    [Theory]
    [InlineData(PricingFallbackReason.SignedOut, "signed out", "Sign in again")]
    [InlineData(PricingFallbackReason.PermissionDenied, "not allowed", "Ask the project owner")]
    [InlineData(PricingFallbackReason.ApiDisabled, "Cloud Billing API", "switch on the Cloud Billing API")]
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
        EstimateReturns(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize));
        var vm = await OpenWithAFileAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        vm.EstimateText.ShouldBeEmpty();
        vm.PriceSourceText.ShouldBeEmpty();
        vm.HasPriceSource.ShouldBeFalse();
    }

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
