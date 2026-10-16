using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #214, wired-to-nothing: the New run page's estimate must be made from the catalog's prices when the catalog answers (the observable
/// is a g2-standard-8 hourly price that is neither the shipped 0.85 nor missing), say so through <see cref="IPricingStatus"/>, and keep the
/// shipped list whenever the catalog cannot be read. The gateway is the scripted fake: nothing here reaches Google.
/// </summary>
public class PricingCatalogWiringTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "deg-pricing-guard-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_appData, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static CatalogSku Sku(string description, string family, string usage, double price, string unit = "h")
        => new("sku-" + description, description, family, "x", usage, ["us-central1"], price, unit);

    private static CatalogSku[] G2Skus() =>
    [
        Sku("G2 Instance Core running in Americas", "Compute", "OnDemand", 0.1),
        Sku("G2 Instance Ram running in Americas", "Compute", "OnDemand", 0.01, "GBy.h"),
        Sku("Nvidia L4 GPU running in Americas", "Compute", "OnDemand", 1.0),
        Sku("Balanced PD Capacity", "Storage", "OnDemand", 0.2, "GBy.mo"),
    ];

    private ServiceProvider Build(Func<FakeGcp, FakeGcp> script)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(_appData, CloudBackend.Developer);
        var provider = services.BuildServiceProvider();
        script(provider.GetRequiredService<FakeGcp>());
        return provider;
    }

    [Fact]
    public async Task The_estimate_uses_the_catalog_price_and_the_page_can_say_so()
    {
        using var provider = Build(gcp => gcp.WithCatalogSkus(G2Skus()));
        var machine = CloudJobRequestFactory.MachineTypeFor(GpuTier.CheapestAvailable);

        var result = await provider.GetRequiredService<ICostEstimateService>().EstimateAsync(machine, spot: false, bases: 5_000, bootDiskGb: 150, TestContext.Current.CancellationToken);

        result.Estimate.ShouldNotBeNull();
        provider.GetRequiredService<IPricingSource>().Load().Table!.Find(machine)!.OnDemandUsdPerHour.ShouldBe((8 * 0.1) + (32 * 0.01) + 1.0, 1e-9);
        var status = provider.GetRequiredService<IPricingStatus>().Current;
        status.Origin.ShouldBe(PricingOrigin.Catalog);
        status.Reason.ShouldBe(PricingFallbackReason.None);
        provider.GetRequiredService<FakeGcp>().CatalogReads.ShouldBe(1);
        File.Exists(Path.Combine(_appData, "pricing-cache.json")).ShouldBeTrue("the daily cache was not written under the app data folder");
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_leaves_the_shipped_price_and_names_why()
    {
        using var provider = Build(gcp => gcp.WithCatalogReadFailing(CloudErrorKind.Permission));
        var machine = CloudJobRequestFactory.MachineTypeFor(GpuTier.CheapestAvailable);

        var result = await provider.GetRequiredService<ICostEstimateService>().EstimateAsync(machine, spot: false, bases: 5_000, bootDiskGb: 150, TestContext.Current.CancellationToken);

        result.Estimate.ShouldNotBeNull("the estimate must still come from the shipped list");
        var status = provider.GetRequiredService<IPricingStatus>().Current;
        status.Origin.ShouldBe(PricingOrigin.Shipped);
        status.Reason.ShouldBe(PricingFallbackReason.PermissionDenied);
    }

    [Fact]
    public async Task The_real_google_build_with_nobody_signed_in_keeps_the_shipped_prices_says_signed_out_and_asks_again_at_once()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(_appData, CloudBackend.Google);
        using var provider = services.BuildServiceProvider();
        var machine = CloudJobRequestFactory.MachineTypeFor(GpuTier.CheapestAvailable);
        var estimates = provider.GetRequiredService<ICostEstimateService>();

        var result = await estimates.EstimateAsync(machine, spot: false, bases: 5_000, bootDiskGb: 150, TestContext.Current.CancellationToken);

        result.Estimate.ShouldNotBeNull();
        var status = provider.GetRequiredService<IPricingStatus>().Current;
        status.Origin.ShouldBe(PricingOrigin.Shipped);
        status.Reason.ShouldBe(PricingFallbackReason.SignedOut);
    }
}
