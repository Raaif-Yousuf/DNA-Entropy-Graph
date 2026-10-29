using System.Text.Json;
using System.Web;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #105: prefix usage, child-prefix listing and prefix delete on the real Cloud Storage gateway over a scripted HTTP
/// handler (Google's documented objects.list and objects.delete shapes, not captured from a live project). The decisive
/// observable is the request: the prefix and delimiter of each list, and that a delete names exactly the listed objects.
/// </summary>
public class GoogleStorageCloudPageTests
{
    private const string Objects = "/storage/v1/b/deg-b/o";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Delete = HttpMethod.Delete;

    private static string ObjectJson(string name, long size) => "{\"kind\":\"storage#object\",\"name\":" + JsonSerializer.Serialize(name) + ",\"size\":\"" + size + "\"}";

    private static string ListJson(string? next, string[] objects, string[]? prefixes = null)
        => "{\"kind\":\"storage#objects\""
            + (objects.Length == 0 ? string.Empty : ",\"items\":[" + string.Join(",", objects) + "]")
            + (prefixes is null ? string.Empty : ",\"prefixes\":[" + string.Join(",", prefixes.Select(p => JsonSerializer.Serialize(p))) + "]")
            + (next is null ? string.Empty : ",\"nextPageToken\":\"" + next + "\"") + "}";

    private static string RpcError(int http, string status, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"}}";

    private static string ObjectPath(string key) => Objects + "/" + Uri.EscapeDataString(key);

    [Fact]
    public async Task Usage_lists_the_prefix_across_pages_and_sums_count_and_bytes()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Objects, 200, ListJson("p2", [ObjectJson("jobs/j1/a", 100), ObjectJson("jobs/j1/b", 250)]))
            .Returns(Get, Objects, 200, ListJson(null, [ObjectJson("jobs/j1/c", 5)]));

        var usage = await rig.Gateways.Storage.GetPrefixUsageAsync("deg-b", "jobs/j1/", CancellationToken.None);

        usage.ShouldBe(new PrefixUsage(3, 355));
        var calls = rig.Handler.To(Get, Objects);
        calls.Count.ShouldBe(2);
        var first = HttpUtility.ParseQueryString(calls[0].Uri.Query);
        first["prefix"].ShouldBe("jobs/j1/");
        first["delimiter"].ShouldBeNull("usage must see every object under the prefix, not a folder view");
        HttpUtility.ParseQueryString(calls[1].Uri.Query)["pageToken"].ShouldBe("p2");
        calls[0].Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
    }

    [Fact]
    public async Task Usage_of_an_empty_prefix_is_zero()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, Objects, 200, ListJson(null, []));

        (await rig.Gateways.Storage.GetPrefixUsageAsync("deg-b", "jobs/none/", CancellationToken.None)).ShouldBe(new PrefixUsage(0, 0));
    }

    [Fact]
    public async Task A_repeated_page_token_fails_the_usage_read_instead_of_returning_a_partial_total()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler
            .Returns(Get, Objects, 200, ListJson("same", [ObjectJson("jobs/j1/a", 1)]))
            .Returns(Get, Objects, 200, ListJson("same", [ObjectJson("jobs/j1/b", 1)]));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.GetPrefixUsageAsync("deg-b", "jobs/j1/", CancellationToken.None));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
    }

    [Fact]
    public async Task DeletePrefix_cut_by_an_IOException_after_one_real_delete_is_a_network_failure()
    {
        // Only GoogleApiException was classified, so a raw IOException left the sweep with no cause. (An HttpRequestException is mapped by CloudCallPipeline, which owns it.)
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler
            .Returns(Get, Objects, 200, ListJson(null, [ObjectJson("jobs/j1/a", 1), ObjectJson("jobs/j1/b", 1)]))
            .Returns(Delete, ObjectPath("jobs/j1/a"), 204, "{}")
            .Calls(Delete, ObjectPath("jobs/j1/b"), _ => throw new IOException("connection reset"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.DeletePrefixAsync("deg-b", "jobs/j1/", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.Requests.Count(r => r.Method == Delete).ShouldBe(2, "the first object really was deleted");
    }

    [Fact]
    public async Task DeletePrefix_cut_by_an_HttpRequestException_reaches_the_caller_as_a_network_failure_through_the_pipeline()
    {
        // The sweep catches IOException and SocketException itself (TRANSPORT_FAILED) but has no arm for HttpRequestException: CloudCallPipeline maps that one. The code is not the sweep's TRANSPORT_FAILED, which would mean a second classifier answered.
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler
            .Returns(Get, Objects, 200, ListJson(null, [ObjectJson("jobs/j1/a", 1), ObjectJson("jobs/j1/b", 1)]))
            .Returns(Delete, ObjectPath("jobs/j1/a"), 204, "{}")
            .Calls(Delete, ObjectPath("jobs/j1/b"), _ => throw new HttpRequestException("no route"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.DeletePrefixAsync("deg-b", "jobs/j1/", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        ex.Error.Code.ShouldNotBe("TRANSPORT_FAILED");
        rig.Handler.Requests.Count(r => r.Method == Delete).ShouldBe(2, "the first object really was deleted, then the second was cut");
    }

    [Fact]
    public async Task ChildPrefixes_asks_for_a_slash_delimited_view_and_returns_the_prefixes_across_pages()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Objects, 200, ListJson("p2", [], ["jobs/j1/", "jobs/j2/"]))
            .Returns(Get, Objects, 200, ListJson(null, [], ["jobs/j3/"]));

        var children = await rig.Gateways.Storage.ListChildPrefixesAsync("deg-b", "jobs/", CancellationToken.None);

        children.ShouldBe(["jobs/j1/", "jobs/j2/", "jobs/j3/"]);
        var query = HttpUtility.ParseQueryString(rig.Handler.To(Get, Objects)[0].Uri.Query);
        query["prefix"].ShouldBe("jobs/");
        query["delimiter"].ShouldBe("/");
    }

    [Fact]
    public async Task DeletePrefix_lists_the_prefix_and_deletes_each_listed_object_by_name_and_returns_the_count()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Objects, 200, ListJson(null, [ObjectJson("jobs/j1/manifest.json", 1), ObjectJson("jobs/j1/output/a b.txt", 2)]))
            .Returns(Delete, ObjectPath("jobs/j1/manifest.json"), 204, "{}")
            .Returns(Delete, ObjectPath("jobs/j1/output/a b.txt"), 204, "{}");

        var deleted = await rig.Gateways.Storage.DeletePrefixAsync("deg-b", "jobs/j1/", CancellationToken.None);

        deleted.ShouldBe(2);
        HttpUtility.ParseQueryString(rig.Handler.To(Get, Objects).Single().Uri.Query)["prefix"].ShouldBe("jobs/j1/");
        rig.Handler.Requests.Count(r => r.Method == Delete).ShouldBe(2);
    }

    [Fact]
    public async Task DeletePrefix_deletes_the_manifest_last_even_though_the_listing_sorts_it_first()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Objects, 200, ListJson(null, [ObjectJson("jobs/j1/manifest.json", 1), ObjectJson("jobs/j1/output/a.txt", 2), ObjectJson("jobs/j1/result.json", 2)]))
            .Returns(Delete, ObjectPath("jobs/j1/manifest.json"), 204, "{}")
            .Returns(Delete, ObjectPath("jobs/j1/output/a.txt"), 204, "{}")
            .Returns(Delete, ObjectPath("jobs/j1/result.json"), 204, "{}");

        var deleted = await rig.Gateways.Storage.DeletePrefixAsync("deg-b", "jobs/j1/", CancellationToken.None);

        deleted.ShouldBe(3);
        var order = rig.Handler.Requests.Where(r => r.Method == Delete).Select(r => r.Uri.AbsolutePath).ToList();
        order.Count.ShouldBe(3);
        order[^1].ShouldContain("manifest.json");
    }

    [Fact]
    public async Task DeletePrefix_deletes_the_manifest_last_when_it_is_on_page_one_and_other_objects_come_on_page_two()
    {
        // Issue #262 review: the manifest is held back across pages, not only within one page.
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Objects, 200, ListJson("p2", [ObjectJson("jobs/j1/manifest.json", 1), ObjectJson("jobs/j1/output/a.txt", 2)]))
            .Returns(Get, Objects, 200, ListJson(null, [ObjectJson("jobs/j1/result.json", 2), ObjectJson("jobs/j1/status.json", 2)]))
            .Returns(Delete, ObjectPath("jobs/j1/manifest.json"), 204, "{}")
            .Returns(Delete, ObjectPath("jobs/j1/output/a.txt"), 204, "{}")
            .Returns(Delete, ObjectPath("jobs/j1/result.json"), 204, "{}")
            .Returns(Delete, ObjectPath("jobs/j1/status.json"), 204, "{}");

        var deleted = await rig.Gateways.Storage.DeletePrefixAsync("deg-b", "jobs/j1/", CancellationToken.None);

        deleted.ShouldBe(4);
        var order = rig.Handler.Requests.Where(r => r.Method == Delete).Select(r => r.Uri.AbsolutePath).ToList();
        order.Count.ShouldBe(4);
        order[^1].ShouldContain("manifest.json");
        HttpUtility.ParseQueryString(rig.Handler.To(Get, Objects)[1].Uri.Query)["pageToken"].ShouldBe("p2");
    }

    [Fact]
    public async Task DeletePrefix_treats_an_object_already_gone_as_deleted_but_a_forbidden_delete_throws()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler
            .Returns(Get, Objects, 200, ListJson(null, [ObjectJson("jobs/j1/a", 1), ObjectJson("jobs/j1/b", 1)]))
            .Returns(Delete, ObjectPath("jobs/j1/a"), 404, RpcError(404, "NOT_FOUND", "No such object"))
            .Returns(Delete, ObjectPath("jobs/j1/b"), 403, RpcError(403, "PERMISSION_DENIED", "user@example.com does not have storage.objects.delete access to the Google Cloud Storage object. Permission 'storage.objects.delete' denied on resource (or it may not exist)."));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.DeletePrefixAsync("deg-b", "jobs/j1/", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("jobs")]
    [InlineData("jobs/../")]
    public async Task DeletePrefix_refuses_a_prefix_that_could_reach_the_whole_bucket_and_sends_nothing(string prefix)
    {
        var rig = new GoogleGatewayHarness();

        await Should.ThrowAsync<ArgumentException>(() => rig.Gateways.Storage.DeletePrefixAsync("deg-b", prefix, CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }
}
