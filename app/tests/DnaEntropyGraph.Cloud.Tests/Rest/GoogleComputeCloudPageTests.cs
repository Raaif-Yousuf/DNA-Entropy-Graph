using System.Text.Json;
using System.Web;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #105: start, the label-filtered disk listing and disk delete on the real Compute gateway, over a scripted HTTP handler
/// (Google's documented REST shapes, not captured from a live project). The decisive observable is the request itself: the path
/// of the start, the label filter of the disk listing, and that no unlabelled disk can come back.
/// </summary>
public class GoogleComputeCloudPageTests
{
    private const string Zone = "us-central1-a";
    private const string Base = "/compute/v1/projects/my-lab";
    private const string Instance = Base + "/zones/" + Zone + "/instances/deg-job1";
    private const string DiskPath = Base + "/zones/" + Zone + "/disks/deg-job1";
    private const string OperationPath = Base + "/zones/" + Zone + "/operations/operation-123";
    private const string AggregatedDisks = Base + "/aggregated/disks";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Delete = HttpMethod.Delete;

    private static string Op(string status, string kind, string? error = null, int? http = null)
        => "{\"kind\":\"compute#operation\",\"name\":\"operation-123\",\"operationType\":\"" + kind + "\",\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + Zone + "\",\"status\":\"" + status + "\""
            + (error is null ? string.Empty : ",\"error\":{\"errors\":[" + error + "]},\"httpErrorStatusCode\":" + http)
            + "}";

    private static string OpError(string code, string message) => "{\"code\":\"" + code + "\",\"message\":" + JsonSerializer.Serialize(message) + "}";

    private static string RpcError(int http, string status, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"}}";

    private static string DiskJson(string name, string zone, string status = "READY", string sizeGb = "150", string install = "installation-1", string app = "dna-entropy-graph", string[]? users = null, bool labels = true)
        => "{\"kind\":\"compute#disk\",\"name\":\"" + name + "\",\"status\":\"" + status + "\",\"sizeGb\":\"" + sizeGb + "\","
            + "\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + zone + "\",\"creationTimestamp\":\"2026-10-03T01:02:03.000-07:00\","
            + (users is null ? string.Empty : "\"users\":[" + string.Join(",", users.Select(u => "\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + zone + "/instances/" + u + "\"")) + "],")
            + (labels ? "\"labels\":{\"app\":\"" + app + "\",\"installation-id\":\"" + install + "\"}" : "\"x\":1")
            + "}";

    private static string Aggregate(string? next, params (string Zone, string[] Disks)[] zones)
        => "{\"kind\":\"compute#diskAggregatedList\",\"items\":{"
            + string.Join(",", zones.Select(z => "\"zones/" + z.Zone + "\":" + (z.Disks.Length == 0
                ? "{\"warning\":{\"code\":\"NO_RESULTS_ON_PAGE\",\"message\":\"none\"}}"
                : "{\"disks\":[" + string.Join(",", z.Disks) + "]}")))
            + "}" + (next is null ? string.Empty : ",\"nextPageToken\":\"" + next + "\"") + "}";

    // ---------------------------------------------------------------- start

    [Fact]
    public async Task Start_posts_to_the_start_path_and_polls_its_operation()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instance + "/start", 200, Op("PENDING", "start"))
            .Returns(Get, OperationPath, 200, Op("DONE", "start"));

        await rig.Gateways.Compute.StartVmAsync("deg-job1", Zone, CancellationToken.None);

        var start = rig.Handler.To(Post, Instance + "/start").Single();
        start.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        start.Uri.Host.ShouldBe("compute.googleapis.com");
        rig.Handler.To(Get, OperationPath).Count.ShouldBe(1);
        rig.Handler.Requests.Count.ShouldBe(2, "a plain start is the start and its poll: no label, scheduling or metadata write (that is reuse)");
    }

    [Fact]
    public async Task Start_of_a_VM_that_is_gone_is_a_VM_NOT_FOUND_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, Instance + "/start", 404, RpcError(404, "NOT_FOUND", "not found"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.StartVmAsync("deg-job1", Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe("VM_NOT_FOUND");
    }

    [Fact]
    public async Task Start_whose_operation_reports_a_stockout_throws_a_stockout()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instance + "/start", 200, Op("PENDING", "start"))
            .Returns(Get, OperationPath, 200, Op("DONE", "start", OpError("ZONE_RESOURCE_POOL_EXHAUSTED", "The zone does not have enough resources available to fulfill the request."), 503));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.StartVmAsync("deg-job1", Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Stockout);
    }

    [Fact]
    public async Task Start_with_no_project_selected_refuses_instead_of_guessing()
    {
        var rig = new GoogleGatewayHarness(selectedProjectId: null);

        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Compute.StartVmAsync("deg-job1", Zone, CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- disks

    [Fact]
    public async Task ListDisks_asks_every_zone_for_both_labels_with_partial_success_and_returns_size_status_and_zone()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, AggregatedDisks, 200, Aggregate(null,
            ("us-central1-a", [DiskJson("deg-job1", "us-central1-a", "READY", "150")]),
            ("europe-west4-a", [DiskJson("deg-job2", "europe-west4-a", "CREATING", "300", users: ["deg-job2"])])));

        var disks = await rig.Gateways.Compute.ListDisksByInstallationAsync("installation-1", CancellationToken.None);

        disks.Select(d => (d.Name, d.Zone, d.SizeGb, d.Status)).ShouldBe([("deg-job1", "us-central1-a", 150, "READY"), ("deg-job2", "europe-west4-a", 300, "CREATING")]);
        disks[0].AttachedTo.ShouldBeEmpty();
        disks[1].AttachedTo.ShouldBe(["deg-job2"]);
        disks[0].CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 3, 8, 2, 3, TimeSpan.Zero));
        var query = HttpUtility.ParseQueryString(rig.Handler.To(Get, AggregatedDisks).Single().Uri.Query);
        query["filter"].ShouldNotBeNull().ShouldContain("labels.installation-id = \"installation-1\"");
        query["filter"]!.ShouldContain("labels.app = \"dna-entropy-graph\"");
        query["returnPartialSuccess"].ShouldBe("true");
    }

    [Fact]
    public async Task ListDisks_drops_a_disk_the_filter_let_through_that_is_not_ours_or_not_this_installations()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, AggregatedDisks, 200, Aggregate(null, ("us-central1-a",
        [
            DiskJson("ours", "us-central1-a"),
            DiskJson("other-installation", "us-central1-a", install: "installation-2"),
            DiskJson("other-app", "us-central1-a", app: "someone-else"),
            DiskJson("no-labels", "us-central1-a", labels: false),
        ])));

        var disks = await rig.Gateways.Compute.ListDisksByInstallationAsync("installation-1", CancellationToken.None);

        disks.Select(d => d.Name).ShouldBe(["ours"]);
    }

    [Fact]
    public async Task ListDisks_follows_the_page_token_and_an_unreachable_zone_fails_the_listing_not_shortens_it()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler
            .Returns(Get, AggregatedDisks, 200, Aggregate("page2", ("us-central1-a", [DiskJson("a", "us-central1-a")])))
            .Returns(Get, AggregatedDisks, 200, Aggregate(null, ("us-east1-b", [DiskJson("b", "us-east1-b")])));

        var disks = await rig.Gateways.Compute.ListDisksByInstallationAsync("installation-1", CancellationToken.None);

        disks.Select(d => d.Name).ShouldBe(["a", "b"]);
        HttpUtility.ParseQueryString(rig.Handler.To(Get, AggregatedDisks)[1].Uri.Query)["pageToken"].ShouldBe("page2");

        var down = new GoogleGatewayHarness(retries: 0);
        down.Handler.Returns(Get, AggregatedDisks, 200, "{\"kind\":\"compute#diskAggregatedList\",\"items\":{},\"unreachables\":[\"zones/us-central1-a\"]}");
        var ex = await Should.ThrowAsync<CloudOperationException>(() => down.Gateways.Compute.ListDisksByInstallationAsync("installation-1", CancellationToken.None));
        ex.Error.Code.ShouldBe("ZONES_UNREACHABLE");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
    }

    [Theory]
    [InlineData("")]
    [InlineData("installation-1\n")]
    [InlineData("a\" OR labels.app:*")]
    public async Task ListDisks_with_an_id_no_label_can_hold_returns_nothing_and_asks_nobody(string installation)
    {
        var rig = new GoogleGatewayHarness();

        var disks = await rig.Gateways.Compute.ListDisksByInstallationAsync(installation, CancellationToken.None);

        disks.ShouldBeEmpty();
        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteDisk_sends_DELETE_on_the_disk_and_polls_its_operation()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Delete, DiskPath, 200, Op("PENDING", "delete"))
            .Returns(Get, OperationPath, 200, Op("DONE", "delete"));

        await rig.Gateways.Compute.DeleteDiskAsync("deg-job1", Zone, CancellationToken.None);

        rig.Handler.To(Delete, DiskPath).Count.ShouldBe(1);
        rig.Handler.To(Get, OperationPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteDisk_of_a_disk_that_is_already_gone_is_a_success_and_a_disk_in_use_is_an_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Delete, DiskPath, 404, RpcError(404, "NOT_FOUND", "not found"))
            .Returns(Delete, DiskPath, 400, RpcError(400, "FAILED_PRECONDITION", "The disk resource is already being used by an instance"));

        await rig.Gateways.Compute.DeleteDiskAsync("deg-job1", Zone, CancellationToken.None);
        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.DeleteDiskAsync("deg-job1", Zone, CancellationToken.None));

        ex.Error.HttpStatus.ShouldBe(400);
    }

    [Fact]
    public async Task DeleteDisk_of_a_regional_disk_uses_the_regional_API_not_the_zonal_one_whose_404_would_read_as_already_gone()
    {
        const string region = "us-central1";
        var regionalPath = Base + "/regions/" + region + "/disks/deg-job1";
        var regionOp = Base + "/regions/" + region + "/operations/operation-123";
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Delete, regionalPath, 200, Op("PENDING", "delete"))
            .Returns(Get, regionOp, 200, Op("DONE", "delete"));

        await rig.Gateways.Compute.DeleteDiskAsync("deg-job1", region, CancellationToken.None);

        rig.Handler.To(Delete, regionalPath).Count.ShouldBe(1);
        rig.Handler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/zones/")).ShouldBeFalse("a zonal delete of a regional disk is a 404 that reads as success");
    }}
