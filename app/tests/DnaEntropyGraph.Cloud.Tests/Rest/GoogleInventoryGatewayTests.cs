using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #60: the two read-only listings the CloudCli needs, over the scripted HTTP handler: every app-labelled VM in the project
/// (no installation filter) and every app-labelled bucket. Payloads are Google's documented REST shapes, not captured from a live project.
/// </summary>
public class GoogleInventoryGatewayTests
{
    private const string Aggregated = "/compute/v1/projects/my-lab/aggregated/instances";
    private const string Buckets = "/storage/v1/b";

    private static string Vm(string name, string zone, string app, string install)
        => "{\"kind\":\"compute#instance\",\"name\":\"" + name + "\",\"status\":\"RUNNING\","
            + "\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + zone + "\",\"creationTimestamp\":\"2026-10-03T01:02:03.000-07:00\","
            + "\"labels\":{\"app\":\"" + app + "\",\"installation-id\":\"" + install + "\"}}";

    private static string Bucket(string name, string app, string install, string location = "US")
        => "{\"kind\":\"storage#bucket\",\"name\":\"" + name + "\",\"location\":\"" + location + "\",\"timeCreated\":\"2026-10-01T10:00:00.000Z\","
            + "\"labels\":{\"app\":\"" + app + "\",\"installation-id\":\"" + install + "\"}}";

    [Fact]
    public async Task ListAppVms_filters_on_the_app_label_only_and_returns_every_installation_across_zones()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            HttpMethod.Get,
            Aggregated,
            200,
            "{\"items\":{\"zones/us-central1-a\":{\"instances\":[" + Vm("deg-a", "us-central1-a", "dna-entropy-graph", "inst-1") + "]},"
            + "\"zones/us-east1-b\":{\"instances\":[" + Vm("deg-b", "us-east1-b", "dna-entropy-graph", "inst-2") + "," + Vm("deg-c", "us-east1-b", "other-app", "inst-2") + "]}}}");

        var vms = await rig.Gateways.Compute.ListAppVmsAsync(CancellationToken.None);

        vms.Select(v => v.Name).ShouldBe(["deg-a", "deg-b"]);
        vms.Select(v => v.Zone).ShouldBe(["us-central1-a", "us-east1-b"]);
        var query = Uri.UnescapeDataString(rig.Handler.To(HttpMethod.Get, Aggregated).Single().Uri.Query);
        query.ShouldContain("labels.app = \"dna-entropy-graph\"");
        query.ShouldNotContain("installation-id");
    }

    [Fact]
    public async Task ListAppVms_with_an_unreachable_zone_throws_instead_of_returning_a_partial_list()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Aggregated, 200, "{\"items\":{},\"unreachables\":[\"zones/us-central1-a\"]}");

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ListAppVmsAsync(CancellationToken.None));

        ex.Error.Code.ShouldBe("ZONES_UNREACHABLE");
    }

    [Fact]
    public async Task ListBuckets_returns_only_buckets_carrying_the_app_label_with_their_labels_location_and_creation_time()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            HttpMethod.Get,
            Buckets,
            200,
            "{\"kind\":\"storage#buckets\",\"items\":[" + Bucket("deg-1-aaaaaa", "dna-entropy-graph", "inst-1", "EU") + "," + Bucket("deg-2-bbbbbb", "other", "inst-1") + "]}");

        var buckets = await rig.Gateways.Storage.ListBucketsAsync("my-lab", CancellationToken.None);

        var bucket = buckets.Single();
        bucket.Name.ShouldBe("deg-1-aaaaaa");
        bucket.Location.ShouldBe("EU");
        bucket.Labels["installation-id"].ShouldBe("inst-1");
        bucket.CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
        var request = rig.Handler.To(HttpMethod.Get, Buckets).Single();
        request.Uri.Query.ShouldContain("project=my-lab");
        request.Uri.Query.ShouldContain("prefix=deg-");
    }

    [Fact]
    public async Task ListBuckets_follows_the_next_page_token_to_the_end()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(HttpMethod.Get, Buckets, 200, "{\"items\":[" + Bucket("deg-1-aaaaaa", "dna-entropy-graph", "inst-1") + "],\"nextPageToken\":\"p2\"}")
            .Returns(HttpMethod.Get, Buckets, 200, "{\"items\":[" + Bucket("deg-2-bbbbbb", "dna-entropy-graph", "inst-1") + "]}");

        var buckets = await rig.Gateways.Storage.ListBucketsAsync("my-lab", CancellationToken.None);

        buckets.Select(b => b.Name).ShouldBe(["deg-1-aaaaaa", "deg-2-bbbbbb"]);
        rig.Handler.To(HttpMethod.Get, Buckets)[1].Uri.Query.ShouldContain("pageToken=p2");
    }

    [Fact]
    public async Task ListBuckets_of_a_project_with_none_is_an_empty_list()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(HttpMethod.Get, Buckets, 200, "{\"kind\":\"storage#buckets\"}");

        (await rig.Gateways.Storage.ListBucketsAsync("my-lab", CancellationToken.None)).ShouldBeEmpty();
    }
}
