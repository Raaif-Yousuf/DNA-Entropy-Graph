using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #214: <see cref="FakeGcp"/> answers the catalog read with the same failure classes the real gateway throws.</summary>
public class FakeGcpPricingCatalogTests
{
    private static CatalogSku Sku(string id) => new(id, "G2 Instance Core running in Americas", "Compute", "CPU", "OnDemand", ["us-central1"], 0.03, "h");

    [Fact]
    public async Task It_returns_the_scripted_skus_and_counts_the_reads()
    {
        var gcp = new FakeGcp().WithCatalogSkus(Sku("a"), Sku("b"));

        (await gcp.ListComputeSkusAsync(CancellationToken.None)).Select(s => s.SkuId).ShouldBe(["a", "b"]);
        gcp.CatalogReads.ShouldBe(1);
    }

    [Fact]
    public async Task With_nothing_scripted_the_catalog_is_empty_not_invented()
    {
        (await new FakeGcp().ListComputeSkusAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission)]
    [InlineData(CloudErrorKind.ApiDisabled)]
    [InlineData(CloudErrorKind.Network)]
    public async Task A_scripted_failure_throws_the_classified_error(CloudErrorKind kind)
    {
        var gcp = new FakeGcp().WithCatalogSkus(Sku("a")).WithCatalogReadFailing(kind);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.ListComputeSkusAsync(CancellationToken.None));

        ex.Kind.ShouldBe(kind);
        gcp.CatalogReads.ShouldBe(1);
    }

    [Fact]
    public async Task A_cloud_that_is_not_connected_cannot_be_read()
    {
        var gcp = new FakeGcp().WithCloudNotConnected();

        await Should.ThrowAsync<CloudOperationException>(() => gcp.ListComputeSkusAsync(CancellationToken.None));
    }
}
