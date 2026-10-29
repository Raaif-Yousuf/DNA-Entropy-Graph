using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #933: every Google list loop in the real gateways fails on a page token it has already seen (the same one for ever, or A, B, A), with
/// <c>LIST_PAGINATION_STUCK</c>, kind Other, within one extra request (A, B, A: the third). The observable is the recorded request count on the
/// route against a scripted HTTP handler whose route never ends. A loop with no guard hits the handler's cap and fails with something else.
/// </summary>
public class ListPagingGuardTests
{
    private const string Compute = "/compute/v1/projects/my-lab";
    private const string Zone = "us-central1-a";

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
        new("VMs", Compute + "/aggregated/instances", n => "{\"items\":{}" + Token(n) + "}", r => r.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None)),
        new("disks", Compute + "/aggregated/disks", n => "{\"items\":{}" + Token(n) + "}", r => r.Gateways.Compute.ListDisksByInstallationAsync("installation-1", CancellationToken.None)),
        new("accelerator zones", Compute + "/aggregated/acceleratorTypes", n => "{\"items\":{}" + Token(n) + "}", r => r.Gateways.Compute.ListAcceleratorZonesAsync("nvidia-l4", CancellationToken.None)),
        new("zone operations", Compute + "/zones/" + Zone + "/operations", n => "{\"items\":[]" + Token(n) + "}", r => r.Gateways.Compute.WasPreemptedAsync("deg-job1", Zone, new VmIncarnation(123UL, null), CancellationToken.None)),
    ];

    public static TheoryData<string> Names() => new(All.Select(l => l.Name));

    private static Listing Find(string name) => All.Single(l => l.Name == name);

    [Fact]
    public void The_table_names_every_list_loop_in_the_rest_gateways()
    {
        All.Length.ShouldBe(11, "one row per loop; the Guards test counts the loops in the source");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task The_same_page_token_for_ever_fails_the_listing_within_one_extra_request(string name)
    {
        var listing = Find(name);
        var rig = new GoogleGatewayHarness();
        rig.Handler.Repeats(HttpMethod.Get, listing.Path, _ => listing.Page("same"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => listing.Call(rig));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        ex.Kind.ShouldBe(CloudErrorKind.Other);
        rig.Handler.To(HttpMethod.Get, listing.Path).Count.ShouldBe(2, "the first page, then the page its token named, which named the same token again; and no retry");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_token_cycle_A_B_A_fails_the_listing_on_the_third_request(string name)
    {
        var listing = Find(name);
        var rig = new GoogleGatewayHarness();
        rig.Handler.Repeats(HttpMethod.Get, listing.Path, n => listing.Page(n % 2 == 0 ? "A" : "B"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => listing.Call(rig));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        ex.Kind.ShouldBe(CloudErrorKind.Other);
        rig.Handler.To(HttpMethod.Get, listing.Path).Count.ShouldBe(3);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_walk_of_distinct_tokens_is_read_to_its_end_and_is_not_stuck(string name)
    {
        var listing = Find(name);
        var rig = new GoogleGatewayHarness();
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
}
