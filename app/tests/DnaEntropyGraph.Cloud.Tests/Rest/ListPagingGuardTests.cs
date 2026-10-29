using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #933: every Google list loop in the real gateways fails on a page token it has already seen (the same one for ever, or A, B, A), with
/// <c>LIST_PAGINATION_STUCK</c>, kind Network, one extra request after the first page (A, B, A: the third). The observable is the recorded request count on the
/// route against a scripted HTTP handler whose route never ends. A loop with no guard hits the handler's cap and fails with something else.
/// Walk tests run with no pipeline retries so the count is one walk; the retry tests below say what the pipeline does with the failure.
/// </summary>
public class ListPagingGuardTests
{
    private const string Compute = "/compute/v1/projects/my-lab";
    private const string Zone = "us-central1-a";
    private const string Aggregated = Compute + "/aggregated/instances";

    private sealed record Listing(string Name, string Path, Func<string?, string> Page, Func<GoogleGatewayHarness, Task> Call);

    private static string Token(string? next) => next is null ? string.Empty : ",\"nextPageToken\":\"" + next + "\"";

    private static readonly Listing[] All =
    [
        new("billing accounts", "/v1/billingAccounts", n => "{\"billingAccounts\":[]" + Token(n) + "}", r => r.Gateways.Billing.ListOpenBillingAccountsAsync(CancellationToken.None)),
        new("projects", "/v3/projects:search", n => "{\"projects\":[]" + Token(n) + "}", r => r.Gateways.ProjectCatalog.ListActiveProjectsAsync(CancellationToken.None)),
        new("price catalog", "/v1/services/6F81-5844-456A/skus", n => "{\"skus\":[]" + Token(n) + "}", r => r.Gateways.PricingCatalog.ListComputeSkusAsync(CancellationToken.None)),
        new("buckets", "/storage/v1/b", n => "{\"items\":[]" + Token(n) + "}", r => r.Gateways.Storage.ListBucketsAsync("my-lab", CancellationToken.None)),
        new("objects", "/storage/v1/b/deg-b/o", n => "{\"kind\":\"storage#objects\"" + Token(n) + "}", r => r.Gateways.Storage.GetPrefixUsageAsync("deg-b", "jobs/j1/", CancellationToken.None)),
        new("quota regions", Compute + "/regions", n => "{\"items\":[]" + Token(n) + "}", async r =>
        {
            var read = await r.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);
            if (read.Failures.FirstOrDefault(f => f.Call == "regions.list") is { } failure)
            {
                throw new CloudOperationException(failure.Error, failure.Kind);
            }
        }),
        new("quota preferences", "/v1/projects/my-lab/locations/global/quotaPreferences", n => "{\"quotaPreferences\":[]" + Token(n) + "}",
            r => r.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", new QuotaIncreaseRequest(QuotaMetrics.NvidiaL4Gpus, "us-central1", 1, "deg-nvidia-l4-gpus-us-central1", "Evo 2 inference for lab research.", "lab@example.org"), CancellationToken.None)),
        new("VMs", Aggregated, n => "{\"items\":{}" + Token(n) + "}", r => r.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None)),
        new("disks", Compute + "/aggregated/disks", n => "{\"items\":{}" + Token(n) + "}", r => r.Gateways.Compute.ListDisksByInstallationAsync("installation-1", CancellationToken.None)),
        new("accelerator zones", Compute + "/aggregated/acceleratorTypes", n => "{\"items\":{}" + Token(n) + "}", r => r.Gateways.Compute.ListAcceleratorZonesAsync("nvidia-l4", CancellationToken.None)),
        new("zone operations", Compute + "/zones/" + Zone + "/operations", n => "{\"items\":[]" + Token(n) + "}", r => r.Gateways.Compute.WasPreemptedAsync("deg-job1", Zone, new VmIncarnation(123UL, null), CancellationToken.None)),
    ];

    public static TheoryData<string> Names() => new(All.Select(l => l.Name));

    private static Listing Find(string name) => All.Single(l => l.Name == name);

    [Theory]
    [MemberData(nameof(Names))]
    public async Task The_same_page_token_for_ever_fails_the_listing_within_one_extra_request(string name)
    {
        var listing = Find(name);
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Repeats(HttpMethod.Get, listing.Path, _ => listing.Page("same"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => listing.Call(rig));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.To(HttpMethod.Get, listing.Path).Count.ShouldBe(2, "the first page, then the page its token named, which named the same token again");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_token_cycle_A_B_A_fails_the_listing_on_the_third_request(string name)
    {
        var listing = Find(name);
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Repeats(HttpMethod.Get, listing.Path, n => listing.Page(n % 2 == 0 ? "A" : "B"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => listing.Call(rig));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.To(HttpMethod.Get, listing.Path).Count.ShouldBe(3);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_walk_of_distinct_tokens_is_read_to_its_end_and_is_not_stuck(string name)
    {
        var listing = Find(name);
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Repeats(HttpMethod.Get, listing.Path, n => listing.Page(n < 3 ? "p" + n : null));

        try
        {
            await listing.Call(rig);
        }
        catch (CloudOperationException ex)
        {
            ex.Error.Code.ShouldNotBe("LIST_PAGINATION_STUCK", "a walk that never repeats a token is not stuck");
        }

        rig.Handler.To(HttpMethod.Get, listing.Path).Count.ShouldBe(4, "three pages that name a next one, then the last");
    }

    [Fact]
    public async Task A_wrapped_listing_whose_token_repeats_once_and_then_recovers_succeeds_on_the_pipelines_replay()
    {
        // Only the billing gateway is wrapped whole in the resilience pipeline (GoogleCloudGateways.Create); kind Network is what makes that replay a fresh walk with a fresh guard.
        var rig = new GoogleGatewayHarness(retries: 3);
        rig.Handler
            .Returns(HttpMethod.Get, "/v1/billingAccounts", 200, "{\"billingAccounts\":[],\"nextPageToken\":\"A\"}")
            .Returns(HttpMethod.Get, "/v1/billingAccounts", 200, "{\"billingAccounts\":[],\"nextPageToken\":\"A\"}")
            .Returns(HttpMethod.Get, "/v1/billingAccounts", 200, "{\"billingAccounts\":[]}");

        var found = await rig.Gateways.Billing.ListOpenBillingAccountsAsync(CancellationToken.None);

        found.ShouldBeEmpty();
        rig.Handler.To(HttpMethod.Get, "/v1/billingAccounts").Count.ShouldBe(3, "walk one: A, A (stuck); the replayed walk: one clean page");
    }

    [Fact]
    public async Task A_wrapped_listing_whose_token_repeats_for_ever_fails_after_the_bounded_replays_and_no_more()
    {
        var rig = new GoogleGatewayHarness(retries: 3);
        rig.Handler.Repeats(HttpMethod.Get, "/v1/billingAccounts", _ => "{\"billingAccounts\":[],\"nextPageToken\":\"same\"}");

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Billing.ListOpenBillingAccountsAsync(CancellationToken.None));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.To(HttpMethod.Get, "/v1/billingAccounts").Count.ShouldBe(8, "1 attempt + 3 replays, 2 requests each (the page, then the page its token named)");
    }

    [Fact]
    public async Task An_unwrapped_listing_is_not_replayed_so_a_stuck_token_costs_two_requests_whatever_the_retry_setting()
    {
        // MEASURED 2026-10-29: the compute gateway is not wrapped whole (each HTTP request has its own retry), so the kind does not change its request count.
        var rig = new GoogleGatewayHarness(retries: 3);
        rig.Handler.Repeats(HttpMethod.Get, Aggregated, _ => "{\"items\":{},\"nextPageToken\":\"same\"}");

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        rig.Handler.To(HttpMethod.Get, Aggregated).Count.ShouldBe(2);
    }
}
