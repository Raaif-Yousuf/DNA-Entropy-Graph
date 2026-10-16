using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #214: the real <see cref="IPricingCatalogGateway"/> over Cloud Billing v1 <c>services.skus.list</c> for Compute Engine, against the
/// scripted HTTP handler. Payloads are the documented Catalog API shape with invented prices, not captured from the live catalog (no GCP access
/// in this build; docs/ToTest.md carries the real-catalog row).
/// </summary>
public class GooglePricingCatalogGatewayTests
{
    private const string Skus = "/v1/services/6F81-5844-456A/skus";

    private static string Sku(string id, string description, string group, string usage, string regions, string rates, string unit = "h", string family = "Compute")
        => "{\"name\":\"services/6F81-5844-456A/skus/" + id + "\",\"skuId\":\"" + id + "\",\"description\":\"" + description
            + "\",\"category\":{\"serviceDisplayName\":\"Compute Engine\",\"resourceFamily\":\"" + family + "\",\"resourceGroup\":\"" + group + "\",\"usageType\":\"" + usage
            + "\"},\"serviceRegions\":[" + regions + "],\"pricingInfo\":[{\"effectiveTime\":\"2026-10-01T00:00:00Z\",\"pricingExpression\":{\"usageUnit\":\"" + unit
            + "\",\"tieredRates\":[" + rates + "]}}]}";

    private static string Rate(double start, string units, int nanos, string currency = "USD")
        => "{\"startUsageAmount\":" + start + ",\"unitPrice\":{\"currencyCode\":\"" + currency + "\",\"units\":\"" + units + "\",\"nanos\":" + nanos + "}}";

    private static string Page(string skus, string? next = null)
        => "{\"skus\":[" + skus + "]" + (next is null ? string.Empty : ",\"nextPageToken\":\"" + next + "\"") + "}";

    private static string Error(int status, string? reason = null)
        => "{\"error\":{\"code\":" + status + ",\"message\":\"" + (status == 403 ? "Required 'billing.skus.list' permission" : "scripted") + "\",\"status\":\"" + (status == 403 ? "PERMISSION_DENIED" : "INTERNAL") + "\""
            + (reason is null ? string.Empty : ",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"" + reason + "\"}]") + "}}";

    [Fact]
    public async Task A_sku_is_read_with_its_category_regions_and_price_from_units_and_nanos()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(HttpMethod.Get, Skus, 200, Page(Sku("AAAA", "G2 Instance Core running in Americas", "CPU", "OnDemand", "\"us-central1\",\"us-east1\"", Rate(0, "0", 31_000_000))));

        var sku = (await rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None)).ShouldHaveSingleItem();

        sku.SkuId.ShouldBe("AAAA");
        sku.Description.ShouldBe("G2 Instance Core running in Americas");
        sku.ResourceFamily.ShouldBe("Compute");
        sku.ResourceGroup.ShouldBe("CPU");
        sku.UsageType.ShouldBe("OnDemand");
        sku.Regions.ShouldBe(["us-central1", "us-east1"]);
        sku.UsdPerUnit.ShouldBe(0.031, 1e-12);
        sku.UsageUnit.ShouldBe("h");
        rig.Handler.Requests.ShouldAllBe(r => r.Authorization == "Bearer " + rig.Tokens.CurrentToken);
    }

    [Fact]
    public async Task Whole_dollars_and_nanos_add_up_and_the_first_tier_is_the_one_that_starts_at_zero()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            HttpMethod.Get,
            Skus,
            200,
            Page(Sku("BBBB", "Nvidia L4 GPU running in Americas", "L4GPU", "OnDemand", "\"us-central1\"", Rate(100, "0", 1) + "," + Rate(0, "1", 250_000_000))));

        (await rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None)).Single().UsdPerUnit.ShouldBe(1.25, 1e-12);
    }

    [Fact]
    public async Task The_request_asks_for_dollars_and_every_page_is_read()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(HttpMethod.Get, Skus, 200, Page(Sku("A1", "G2 Instance Core running in Americas", "CPU", "OnDemand", "\"us-central1\"", Rate(0, "0", 1_000_000)), "p2"));
        rig.Handler.Returns(HttpMethod.Get, Skus, 200, Page(Sku("A2", "G2 Instance Ram running in Americas", "RAM", "OnDemand", "\"us-central1\"", Rate(0, "0", 2_000_000), "GBy.h")));

        var all = await rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None);

        all.Select(s => s.SkuId).ShouldBe(["A1", "A2"]);
        var calls = rig.Handler.To(HttpMethod.Get, Skus);
        calls.ShouldAllBe(c => c.Uri.Query.Contains("currencyCode=USD"));
        calls[0].Uri.Query.ShouldNotContain("pageToken");
        calls[1].Uri.Query.ShouldContain("pageToken=p2");
    }

    [Fact]
    public async Task A_sku_with_no_usable_dollar_price_is_left_out_not_priced_at_zero()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            HttpMethod.Get,
            Skus,
            200,
            Page(
                "{\"skuId\":\"NOPRICE\",\"description\":\"x\",\"category\":{\"resourceFamily\":\"Compute\"},\"serviceRegions\":[\"us-central1\"],\"pricingInfo\":[]},"
                + Sku("EUR", "G2 Instance Core running in Americas", "CPU", "OnDemand", "\"us-central1\"", Rate(0, "0", 5, "EUR")) + ","
                + Sku("GOOD", "G2 Instance Core running in Americas", "CPU", "OnDemand", "\"us-central1\"", Rate(0, "0", 5))));

        (await rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None)).Select(s => s.SkuId).ShouldBe(["GOOD"]);
    }

    [Fact]
    public async Task A_repeated_page_token_fails_the_read_rather_than_returning_a_partial_catalog()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Skus, 200, Page(Sku("A1", "d", "CPU", "OnDemand", "\"us-central1\"", Rate(0, "0", 1)), "same"));
        rig.Handler.Returns(HttpMethod.Get, Skus, 200, Page(Sku("A2", "d", "CPU", "OnDemand", "\"us-central1\"", Rate(0, "0", 1)), "same"));
        rig.Handler.Returns(HttpMethod.Get, Skus, 200, Page(Sku("A3", "d", "CPU", "OnDemand", "\"us-central1\"", Rate(0, "0", 1)), "same"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
    }

    [Fact]
    public async Task A_403_is_a_permission_error()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Skus, 403, Error(403));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task A_disabled_billing_api_is_an_api_disabled_error()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Skus, 403, Error(403, "SERVICE_DISABLED"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.ApiDisabled);
    }

    [Fact]
    public async Task A_500_is_a_network_class_error()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Skus, 500, Error(500));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
    }
}
