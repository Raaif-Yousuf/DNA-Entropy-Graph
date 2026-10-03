using System.Net;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #53: the real Cloud Storage v1 gateway (results bucket, and the object calls of <see cref="IStorageGateway"/>)
/// over a scripted HTTP handler. Payloads are Google's documented JSON API shapes (bucket resource, error body), not
/// captured from a live project: there is no GCP access in this build. The decisive observables are the buckets.insert
/// body (lifecycle, UBLA, PAP, labels) and that a read-back which disagrees fails with a named error.
/// </summary>
public class GoogleStorageGatewayTests
{
    private const string Project = "/v3/projects/my-lab";
    private const string Buckets = "/storage/v1/b";
    private const string Number = "123456789012";
    private const string ObjectPath = "/storage/v1/b/deg-b/o/jobs%2Fj1%2Fresult.json";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Patch = HttpMethod.Patch;
    private static readonly HttpMethod Put = HttpMethod.Put;

    /// <summary>The names the harness's seeded Random hands out, in order: the same seed and the same six draws per name.</summary>
    private static string Name(int index)
    {
        var random = new Random(53);
        var name = string.Empty;
        for (var i = 0; i <= index; i++)
        {
            name = ResultsBucket.NewName(Number, random);
        }

        return name;
    }

    private static string ProjectBody() => "{\"name\":\"projects/" + Number + "\",\"projectId\":\"my-lab\",\"state\":\"ACTIVE\",\"displayName\":\"Lab\"}";

    private static string RpcError(int http, string status, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"}}";

    /// <summary>Cloud Storage's own error shape (not google.rpc): the machine-readable reason is in <c>errors[].reason</c>.</summary>
    private static string StorageError(int http, string reason, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"errors\":[{\"message\":" + JsonSerializer.Serialize(message) + ",\"domain\":\"global\",\"reason\":\"" + reason + "\"}]}}";

    /// <summary>A bucket resource as Google returns it. Pass null to leave a piece out (what a bucket that never got the setting looks like).</summary>
    private static string BucketJson(
        string name,
        string installation = "installation-1",
        bool labelled = true,
        bool ubla = true,
        string? pap = "enforced",
        int? jobsAge = 30,
        int? cacheAge = 365,
        string created = "2026-10-01T10:00:00.000Z",
        string? extraRules = null)
    {
        var labels = labelled
            ? "{\"app\":\"dna-entropy-graph\",\"installation-id\":\"" + installation + "\",\"app-version\":\"0-1-0\",\"lifecycle\":\"results\"}"
            : "{}";
        var rules = new List<string>();
        if (jobsAge is { } j)
        {
            rules.Add("{\"action\":{\"type\":\"Delete\"},\"condition\":{\"age\":" + j + ",\"matchesPrefix\":[\"jobs/\"]}}");
        }

        if (cacheAge is { } c)
        {
            rules.Add("{\"action\":{\"type\":\"Delete\"},\"condition\":{\"age\":" + c + ",\"matchesPrefix\":[\"cache/\"]}}");
        }

        if (extraRules is not null)
        {
            rules.Add(extraRules);
        }

        return "{\"kind\":\"storage#bucket\",\"name\":\"" + name + "\",\"location\":\"US\",\"timeCreated\":\"" + created + "\","
            + "\"labels\":" + labels + ","
            + "\"iamConfiguration\":{\"uniformBucketLevelAccess\":{\"enabled\":" + (ubla ? "true" : "false") + "}"
            + (pap is null ? string.Empty : ",\"publicAccessPrevention\":\"" + pap + "\"") + "},"
            + "\"lifecycle\":{\"rule\":[" + string.Join(",", rules) + "]}}";
    }

    private static string List(params string[] buckets) => "{\"kind\":\"storage#buckets\",\"items\":[" + string.Join(",", buckets) + "]}";

    private static string BucketPath(string name) => Buckets + "/" + name;

    private static string UploadPath(string bucket) => "/upload/storage/v1/b/" + bucket + "/o";

    private static string InsertedName(ScriptedHttpHandler handler, int index = 0)
    {
        using var body = JsonDocument.Parse(handler.To(Post, Buckets)[index].Body);
        return body.RootElement.GetProperty("name").GetString()!;
    }

    private static int RuleAge(JsonElement root, string prefix)
        => root.GetProperty("lifecycle").GetProperty("rule").EnumerateArray()
            .Single(r => r.GetProperty("action").GetProperty("type").GetString() == "Delete" && r.GetProperty("condition").GetProperty("matchesPrefix")[0].GetString() == prefix)
            .GetProperty("condition").GetProperty("age").GetInt32();

    /// <summary>Scripts the resumable upload of one object: the initiating POST answers with the session URL, the PUT stores it.</summary>
    private static void ScriptUpload(ScriptedHttpHandler handler, string bucket)
    {
        handler.Calls(Post, UploadPath(bucket), _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response.Headers.Location = new Uri("https://storage.googleapis.com" + UploadPath(bucket) + "?uploadType=resumable&upload_id=sess-1");
            return Task.FromResult(response);
        });
        handler.Returns(Put, UploadPath(bucket), 200, "{\"name\":\"app-config.json\",\"bucket\":\"" + bucket + "\"}");
    }

    /// <summary>The conditional config write (ifGenerationMatch=0) of a bucket whose app-config.json already exists: Google answers 412 and that file stands.</summary>
    private static void ScriptConfigAlreadyThere(ScriptedHttpHandler handler, string bucket)
        => handler.Returns(Post, UploadPath(bucket), 412, StorageError(412, "conditionNotMet", "At least one of the pre-conditions you specified did not hold."));

    /// <summary>The listing the call makes right after its own insert (to notice a rival that created one too): just the bucket it made.</summary>
    private static void ScriptCreatedListing(ScriptedHttpHandler handler, string created)
        => handler.Returns(Get, Buckets, 200, List(BucketJson(created)));

    private static GoogleGatewayHarness NewRig(int retentionDays = 30, string? installationId = "installation-1")
    {
        var rig = new GoogleGatewayHarness(retries: 2, installationId: installationId, retentionDays: retentionDays);
        rig.Handler.Returns(Get, Project, 200, ProjectBody());
        return rig;
    }

    [Fact]
    public async Task Creating_inserts_a_bucket_with_both_lifecycle_rules_UBLA_PAP_enforced_and_the_labels_then_reads_it_back()
    {
        var rig = NewRig(retentionDays: 30);
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, BucketJson(name));
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name));
        ScriptUpload(rig.Handler, name);
        ScriptCreatedListing(rig.Handler, name);

        var bucket = await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        bucket.ShouldBe(name);
        name.ShouldStartWith("deg-" + Number + "-");
        var insert = rig.Handler.To(Post, Buckets).Single();
        insert.Uri.Host.ShouldBe("storage.googleapis.com");
        insert.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        insert.Uri.Query.ShouldContain("project=my-lab");
        using var body = JsonDocument.Parse(insert.Body);
        var root = body.RootElement;
        root.GetProperty("name").GetString().ShouldBe(name);
        root.GetProperty("location").GetString().ShouldBe("US");
        var iam = root.GetProperty("iamConfiguration");
        iam.GetProperty("uniformBucketLevelAccess").GetProperty("enabled").GetBoolean().ShouldBeTrue();
        iam.GetProperty("publicAccessPrevention").GetString().ShouldBe("enforced");
        var labels = root.GetProperty("labels");
        labels.GetProperty("app").GetString().ShouldBe("dna-entropy-graph");
        labels.GetProperty("installation-id").GetString().ShouldBe("installation-1");
        labels.GetProperty("app-version").GetString().ShouldBe("0-1-0");
        labels.GetProperty("lifecycle").GetString().ShouldBe("results");
        root.GetProperty("lifecycle").GetProperty("rule").GetArrayLength().ShouldBe(2);
        RuleAge(root, "jobs/").ShouldBe(30);
        RuleAge(root, "cache/").ShouldBe(365);
        root.GetProperty("lifecycle").GetProperty("rule").EnumerateArray()
            .Select(r => r.GetProperty("action").GetProperty("type").GetString()).ShouldAllBe(t => t == "Delete");

        rig.Handler.To(Get, BucketPath(name)).Count.ShouldBe(1);
        var config = rig.Handler.To(Put, UploadPath(name)).Single();
        using var configBody = JsonDocument.Parse(config.Body);
        configBody.RootElement.GetProperty("resultsRetentionDays").GetInt32().ShouldBe(30);
        configBody.RootElement.GetProperty("installationId").GetString().ShouldBe("installation-1");
        rig.Handler.To(Post, UploadPath(name)).Single().Uri.Query.ShouldContain("ifGenerationMatch=0");
    }

    [Fact]
    public async Task The_retention_in_the_lifecycle_rule_is_the_configured_one()
    {
        var rig = NewRig(retentionDays: 365);
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name, jobsAge: 365));
        ScriptUpload(rig.Handler, name);
        ScriptCreatedListing(rig.Handler, name);

        await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Post, Buckets).Single().Body);
        RuleAge(body.RootElement, "jobs/").ShouldBe(365);
    }

    [Fact]
    public async Task A_read_back_without_the_jobs_rule_fails_with_the_named_error_and_does_not_return_the_bucket()
    {
        var rig = NewRig();
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name, jobsAge: null));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.BucketConfigNotApplied);
        ex.Message.ShouldContain("jobs/");
        rig.Handler.To(Post, UploadPath(name)).ShouldBeEmpty("a bucket that is not what was asked for is not given a config file");
    }

    [Theory]
    [InlineData("ubla", "uniform bucket-level access")]
    [InlineData("pap", "public access prevention")]
    [InlineData("jobs-age", "jobs/")]
    [InlineData("cache-rule", "cache/")]
    public async Task A_read_back_that_disagrees_on_any_one_setting_names_that_setting(string broken, string mentioned)
    {
        var rig = NewRig();
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        var readBack = broken switch
        {
            "ubla" => BucketJson(name, ubla: false),
            "pap" => BucketJson(name, pap: "inherited"),
            "jobs-age" => BucketJson(name, jobsAge: 7),
            _ => BucketJson(name, cacheAge: null),
        };
        rig.Handler.Returns(Get, BucketPath(name), 200, readBack);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.BucketConfigNotApplied);
        ex.Message.ShouldContain(mentioned);
    }

    [Fact]
    public async Task A_correct_read_back_does_not_trip_any_of_the_mismatch_checks()
    {
        // Vacuity guard for the theory above: the same fixture builder with nothing broken passes, so each case there fails for its one broken setting.
        var rig = NewRig();
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name));
        ScriptUpload(rig.Handler, name);
        ScriptCreatedListing(rig.Handler, name);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(name);
    }

    [Fact]
    public async Task An_existing_labelled_bucket_is_adopted_with_no_insert_no_patch_and_no_config_overwrite()
    {
        var rig = NewRig();
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing)));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        var bucket = await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        bucket.ShouldBe(existing);
        rig.Handler.To(Post, Buckets).ShouldBeEmpty();
        rig.Handler.To(Patch, BucketPath(existing)).ShouldBeEmpty();
        rig.Handler.To(Put, UploadPath(existing)).ShouldBeEmpty();
        rig.Handler.To(Post, UploadPath(existing)).Single().Uri.Query.ShouldContain("ifGenerationMatch=0");
        rig.Handler.Requests.ShouldNotContain(r => r.Method == HttpMethod.Delete, "a bucket this call did not create is never deleted");
        var list = rig.Handler.To(Get, Buckets).Single();
        list.Uri.Query.ShouldContain("prefix=deg-");
        list.Uri.Query.ShouldContain("project=my-lab");
    }

    [Fact]
    public async Task A_bucket_with_the_deg_prefix_but_without_our_label_is_not_adopted()
    {
        var rig = NewRig();
        var foreign = "deg-" + Number + "-zzzzzz";
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(foreign, labelled: false)));
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name));
        ScriptUpload(rig.Handler, name);
        ScriptCreatedListing(rig.Handler, name);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(name);
    }

    [Fact]
    public async Task A_second_computer_in_the_same_project_adopts_the_bucket_another_installation_made()
    {
        var rig = NewRig(installationId: "installation-2");
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, installation: "installation-1")));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(existing);
        rig.Handler.To(Post, Buckets).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_several_labelled_buckets_ours_by_installation_wins_then_the_oldest()
    {
        var oldest = "deg-" + Number + "-aaaaaa";
        var mine = "deg-" + Number + "-bbbbbb";
        var newer = "deg-" + Number + "-cccccc";

        var rig = NewRig(installationId: "installation-2");
        rig.Handler.Returns(Get, Buckets, 200, List(
            BucketJson(newer, installation: "installation-3", created: "2026-10-03T10:00:00.000Z"),
            BucketJson(oldest, installation: "installation-1", created: "2026-09-01T10:00:00.000Z"),
            BucketJson(mine, installation: "installation-2", created: "2026-10-02T10:00:00.000Z")));
        ScriptConfigAlreadyThere(rig.Handler, mine);
        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(mine);

        var other = NewRig(installationId: "installation-9");
        other.Handler.Returns(Get, Buckets, 200, List(
            BucketJson(newer, installation: "installation-3", created: "2026-10-03T10:00:00.000Z"),
            BucketJson(oldest, installation: "installation-1", created: "2026-09-01T10:00:00.000Z")));
        ScriptConfigAlreadyThere(other.Handler, oldest);
        (await other.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(oldest);
    }

    [Fact]
    public async Task An_adopted_bucket_whose_retention_drifted_is_patched_then_read_back()
    {
        var rig = NewRig(retentionDays: 30);
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 7)));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing, jobsAge: 30));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(existing);

        using var body = JsonDocument.Parse(rig.Handler.To(Patch, BucketPath(existing)).Single().Body);
        RuleAge(body.RootElement, "jobs/").ShouldBe(30);
        rig.Handler.To(Get, BucketPath(existing)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_patch_that_leaves_the_bucket_wrong_fails_with_the_named_error()
    {
        var rig = NewRig(retentionDays: 30);
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 7)));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing, jobsAge: 7));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.BucketConfigNotApplied);
    }

    [Fact]
    public async Task A_taken_name_regenerates_the_suffix_and_inserts_again()
    {
        var rig = NewRig();
        var first = Name(0);
        var second = Name(1);
        first.ShouldNotBe(second);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 409, RpcError(409, "ALREADY_EXISTS", "Your previous request to create the named bucket succeeded and you already own it."));
        rig.Handler.Returns(Get, BucketPath(first), 403, RpcError(403, "PERMISSION_DENIED", "not ours to read"));
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(second), 200, BucketJson(second));
        ScriptUpload(rig.Handler, second);
        ScriptCreatedListing(rig.Handler, second);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(second);

        InsertedName(rig.Handler, 0).ShouldBe(first);
        InsertedName(rig.Handler, 1).ShouldBe(second);
    }

    [Fact]
    public async Task A_409_for_a_bucket_that_is_ours_is_a_replayed_insert_and_is_adopted_not_regenerated()
    {
        var rig = NewRig();
        var first = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 409, RpcError(409, "ALREADY_EXISTS", "You already own this bucket."));
        rig.Handler.Returns(Get, BucketPath(first), 200, BucketJson(first));
        ScriptUpload(rig.Handler, first);
        ScriptCreatedListing(rig.Handler, first);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(first);

        rig.Handler.To(Post, Buckets).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Names_taken_every_time_end_after_five_attempts_as_BUCKET_NAME_TAKEN()
    {
        var rig = NewRig();
        rig.Handler.Returns(Get, Buckets, 200, List());
        for (var i = 0; i < 5; i++)
        {
            rig.Handler.Returns(Post, Buckets, 409, RpcError(409, "ALREADY_EXISTS", "taken"));
            rig.Handler.Returns(Get, BucketPath(Name(i)), 403, RpcError(403, "PERMISSION_DENIED", "not yours"));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.BucketNameTaken);
        ex.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
        rig.Handler.To(Post, Buckets).Count.ShouldBe(5);
        Enumerable.Range(0, 5).Select(i => InsertedName(rig.Handler, i)).Distinct().Count().ShouldBe(5);
    }

    [Fact]
    public async Task A_permission_refusal_on_insert_is_not_retried_and_is_a_permission_error()
    {
        var rig = NewRig();
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 403, RpcError(403, "PERMISSION_DENIED", "user@example.com does not have storage.buckets.create access to the Google Cloud project. Permission 'storage.buckets.create' denied on resource (or it may not exist)."));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.To(Post, Buckets).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_transient_503_on_the_list_is_retried_alone_and_the_insert_is_sent_once()
    {
        var rig = NewRig();
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 503, RpcError(503, "UNAVAILABLE", "try later"));
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name));
        ScriptUpload(rig.Handler, name);
        ScriptCreatedListing(rig.Handler, name);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(name);

        rig.Handler.To(Get, Buckets).Count.ShouldBe(3, "the failed list, the retried one, and the look after the insert");
        rig.Handler.To(Post, Buckets).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_project_the_account_cannot_see_stops_before_any_bucket_call()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Get, Project, 404, RpcError(404, "NOT_FOUND", "no such project"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_an_installation_id_nothing_is_sent_because_a_bucket_without_the_label_would_be_invisible()
    {
        var rig = NewRig(installationId: null);

        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_config_write_that_fails_is_written_by_the_next_call_that_adopts_the_bucket()
    {
        var rig = NewRig();
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name));
        ScriptCreatedListing(rig.Handler, name);
        for (var i = 0; i < 3; i++)
        {
            rig.Handler.Returns(Post, UploadPath(name), 503, RpcError(503, "UNAVAILABLE", "try later"));
        }

        await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));
        rig.Handler.To(Put, UploadPath(name)).ShouldBeEmpty("the first call never stored the config");

        rig.Handler.Returns(Get, Project, 200, ProjectBody());
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(name)));
        ScriptUpload(rig.Handler, name);
        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(name);

        using var config = JsonDocument.Parse(rig.Handler.To(Put, UploadPath(name)).Single().Body);
        config.RootElement.GetProperty("installationId").GetString().ShouldBe("installation-1");
        rig.Handler.To(Post, Buckets).Count.ShouldBe(1, "the second call adopted, it did not insert again");
    }

    [Fact]
    public async Task Two_computers_that_both_created_a_bucket_converge_on_the_older_and_the_newer_one_is_deleted()
    {
        var rig = NewRig();
        var mine = Name(0);
        var older = "deg-" + Number + "-aaaaaa";
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(mine), 200, BucketJson(mine, created: "2026-10-03T10:00:00.000Z"));
        rig.Handler.Returns(Get, Buckets, 200, List(
            BucketJson(mine, created: "2026-10-03T10:00:00.000Z"),
            BucketJson(older, created: "2026-10-01T10:00:00.000Z")));
        rig.Handler.Returns(HttpMethod.Delete, BucketPath(mine), 204, string.Empty);
        ScriptUpload(rig.Handler, older);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(older);

        rig.Handler.To(HttpMethod.Delete, BucketPath(mine)).Count.ShouldBe(1);
        rig.Handler.Requests.Count(r => r.Method == HttpMethod.Delete).ShouldBe(1);
        rig.Handler.To(Post, UploadPath(mine)).ShouldBeEmpty("the bucket being thrown away is never given a config file");
        rig.Handler.To(Put, UploadPath(older)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_bucket_this_call_created_that_is_still_the_preferred_one_is_kept_and_not_deleted()
    {
        var rig = NewRig();
        var mine = Name(0);
        var newer = "deg-" + Number + "-zzzzzz";
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(mine), 200, BucketJson(mine, created: "2026-10-01T10:00:00.000Z"));
        rig.Handler.Returns(Get, Buckets, 200, List(
            BucketJson(newer, created: "2026-10-03T10:00:00.000Z"),
            BucketJson(mine, created: "2026-10-01T10:00:00.000Z")));
        ScriptUpload(rig.Handler, mine);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(mine);

        rig.Handler.Requests.ShouldNotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_delete_of_the_losing_bucket_that_fails_does_not_fail_the_call()
    {
        var rig = NewRig();
        var mine = Name(0);
        var older = "deg-" + Number + "-aaaaaa";
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(mine), 200, BucketJson(mine, created: "2026-10-03T10:00:00.000Z"));
        rig.Handler.Returns(Get, Buckets, 200, List(
            BucketJson(mine, created: "2026-10-03T10:00:00.000Z"),
            BucketJson(older, created: "2026-10-01T10:00:00.000Z")));
        rig.Handler.Returns(HttpMethod.Delete, BucketPath(mine), 409, RpcError(409, "FAILED_PRECONDITION", "The bucket you tried to delete is not empty."));
        ScriptUpload(rig.Handler, older);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(older);

        rig.Handler.To(HttpMethod.Delete, BucketPath(mine)).Count.ShouldBe(1, "the losing bucket is deleted once, not retried and not skipped");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100000)]
    public async Task A_retention_outside_the_allowed_range_is_refused_before_any_request(int days)
    {
        var rig = NewRig(retentionDays: days);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_patch_keeps_the_lifecycle_rules_the_user_added_and_replaces_only_ours()
    {
        var rig = NewRig(retentionDays: 30);
        var existing = "deg-" + Number + "-abcdef";
        const string scratch = "{\"action\":{\"type\":\"Delete\"},\"condition\":{\"age\":7,\"matchesPrefix\":[\"scratch/\"]}}";
        const string nearline = "{\"action\":{\"type\":\"SetStorageClass\",\"storageClass\":\"NEARLINE\"},\"condition\":{\"age\":10,\"matchesPrefix\":[\"jobs/\"]}}";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 7, extraRules: scratch + "," + nearline)));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing, jobsAge: 30, extraRules: scratch + "," + nearline));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Patch, BucketPath(existing)).Single().Body);
        var rules = body.RootElement.GetProperty("lifecycle").GetProperty("rule").EnumerateArray().ToList();
        rules.Count.ShouldBe(4);
        rules.ShouldContain(r => r.GetProperty("condition").GetProperty("matchesPrefix")[0].GetString() == "scratch/" && r.GetProperty("condition").GetProperty("age").GetInt32() == 7);
        rules.ShouldContain(r => r.GetProperty("action").GetProperty("type").GetString() == "SetStorageClass");
        rules.Count(r => r.GetProperty("action").GetProperty("type").GetString() == "Delete" && r.GetProperty("condition").GetProperty("matchesPrefix")[0].GetString() == "jobs/").ShouldBe(1);
        RuleAge(body.RootElement, "jobs/").ShouldBe(30);
    }
    // ------------------------------------------------------------------ round 3

    [Fact]
    public async Task A_shorter_retention_never_shortens_a_longer_one_already_on_the_bucket()
    {
        var rig = NewRig(retentionDays: 7);
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 90)));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(existing);

        rig.Handler.To(Patch, BucketPath(existing)).ShouldBeEmpty("a 90 day bucket is not drifted for a caller that asked for 7: patching it would delete the other installation''s results early");
    }

    [Fact]
    public async Task A_longer_retention_lengthens_the_bucket_and_repeated_calls_then_send_no_patch()
    {
        var rig = NewRig(retentionDays: 120);
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 90)));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing, jobsAge: 120));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Patch, BucketPath(existing)).Single().Body);
        RuleAge(body.RootElement, "jobs/").ShouldBe(120);

        // The same caller again, and then a caller at 7: the bucket now says 120, neither patches.
        for (var i = 0; i < 2; i++)
        {
            rig.Handler.Returns(Get, Project, 200, ProjectBody());
            rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 120)));
            ScriptConfigAlreadyThere(rig.Handler, existing);
            await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);
        }

        rig.Handler.To(Patch, BucketPath(existing)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_patch_for_another_reason_keeps_the_longer_jobs_age_it_found()
    {
        var rig = NewRig(retentionDays: 7);
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 90, ubla: false)));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing, jobsAge: 90));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Patch, BucketPath(existing)).Single().Body);
        RuleAge(body.RootElement, "jobs/").ShouldBe(90);
    }

    [Fact]
    public async Task Two_different_installations_that_both_created_a_bucket_converge_on_the_older_one_whichever_it_belongs_to()
    {
        var rig = NewRig(installationId: "installation-2");
        var mine = Name(0);
        var older = "deg-" + Number + "-aaaaaa";
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(mine), 200, BucketJson(mine, installation: "installation-2", created: "2026-10-03T10:00:00.000Z"));
        rig.Handler.Returns(Get, Buckets, 200, List(
            BucketJson(mine, installation: "installation-2", created: "2026-10-03T10:00:00.000Z"),
            BucketJson(older, installation: "installation-1", created: "2026-10-01T10:00:00.000Z")));
        rig.Handler.Returns(HttpMethod.Delete, BucketPath(mine), 204, string.Empty);
        ScriptUpload(rig.Handler, older);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(older);

        rig.Handler.To(HttpMethod.Delete, BucketPath(mine)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_precondition_failure_on_the_config_write_means_the_file_is_there_and_is_swallowed()
    {
        var rig = NewRig();
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing)));
        rig.Handler.Returns(Post, UploadPath(existing), 412, StorageError(412, "conditionNotMet", "At least one of the pre-conditions you specified did not hold."));

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(existing);
    }

    [Fact]
    public async Task A_412_that_is_an_organization_policy_refusal_surfaces_instead_of_reading_as_the_file_being_there()
    {
        var rig = NewRig();
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing)));
        rig.Handler.Returns(Post, UploadPath(existing), 412, RpcError(412, "FAILED_PRECONDITION", "Request violates constraint constraints/storage.retentionPolicySeconds."));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
    }

    [Fact]
    public async Task An_upload_retried_after_a_503_replays_the_exact_bytes_from_the_start()
    {
        var rig = NewRig();
        rig.Handler.Returns(Post, UploadPath("deg-b"), 503, RpcError(503, "UNAVAILABLE", "try later"));
        ScriptUpload(rig.Handler, "deg-b");

        await rig.Gateways.Storage.UploadAsync("deg-b", "jobs/j1/manifest.json", new MemoryStream(Encoding.UTF8.GetBytes("0123456789")), CancellationToken.None);

        rig.Handler.To(Post, UploadPath("deg-b")).Count.ShouldBe(2);
        rig.Handler.To(Put, UploadPath("deg-b")).Single().Body.ShouldBe("0123456789");
    }

    [Fact]
    public async Task An_upload_that_fails_part_way_through_the_stream_is_retried_with_every_byte_from_position_zero()
    {
        var rig = NewRig();
        rig.Handler.FailsMidBody(Put, UploadPath("deg-b"), bytesRead: 4)
            .FailsMidBody(Put, UploadPath("deg-b"), bytesRead: 4)
            .FailsMidBody(Put, UploadPath("deg-b"), bytesRead: 4);
        // The data PUT dies after 4 bytes and so do the Google client's own resume queries, so the failure reaches the pipeline,
        // which must start the whole upload again from the first byte (the stream now stands at 4 or later).
        ScriptUpload(rig.Handler, "deg-b");
        ScriptUpload(rig.Handler, "deg-b");
        var content = new MemoryStream(Encoding.UTF8.GetBytes("0123456789"));

        await rig.Gateways.Storage.UploadAsync("deg-b", "jobs/j1/manifest.json", content, CancellationToken.None);

        rig.Handler.To(Post, UploadPath("deg-b")).Count.ShouldBe(2);
        var puts = rig.Handler.To(Put, UploadPath("deg-b"));
        puts[0].Body.ShouldBe("0123");
        puts[^1].Body.ShouldBe("0123456789");
    }

    [Fact]
    public async Task A_longer_cache_age_on_the_bucket_is_not_drift_and_is_not_patched()
    {
        var rig = NewRig(retentionDays: 30);
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 30, cacheAge: 400)));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(existing);

        rig.Handler.To(Patch, BucketPath(existing)).ShouldBeEmpty("a cache/ rule at 400 days is another installation's choice; shortening it to 365 would delete its cache early");
    }

    [Fact]
    public async Task A_patch_for_another_reason_keeps_the_longer_cache_age_it_found()
    {
        var rig = NewRig(retentionDays: 30);
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: 7, cacheAge: 400)));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing, jobsAge: 30, cacheAge: 400));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Patch, BucketPath(existing)).Single().Body);
        RuleAge(body.RootElement, "jobs/").ShouldBe(30);
        RuleAge(body.RootElement, "cache/").ShouldBe(400);
    }

    [Fact]
    public async Task A_jobs_rule_with_extra_conditions_is_the_users_own_so_it_is_neither_counted_toward_our_age_nor_replaced()
    {
        var rig = NewRig(retentionDays: 30);
        var existing = "deg-" + Number + "-abcdef";
        const string users = "{\"action\":{\"type\":\"Delete\"},\"condition\":{\"age\":90,\"matchesPrefix\":[\"jobs/\"],\"matchesStorageClass\":[\"NEARLINE\"]}}";
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing, jobsAge: null, extraRules: users)));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing, jobsAge: 30, extraRules: users));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Patch, BucketPath(existing)).Single().Body);
        var jobsDeletes = body.RootElement.GetProperty("lifecycle").GetProperty("rule").EnumerateArray()
            .Where(r => r.GetProperty("action").GetProperty("type").GetString() == "Delete" && r.GetProperty("condition").GetProperty("matchesPrefix")[0].GetString() == "jobs/")
            .ToList();
        jobsDeletes.Count.ShouldBe(2, "ours is added and the user's stays");
        jobsDeletes.Any(r => r.GetProperty("condition").TryGetProperty("matchesStorageClass", out var _) && r.GetProperty("condition").GetProperty("age").GetInt32() == 90).ShouldBeTrue();
        jobsDeletes.Any(r => !r.GetProperty("condition").TryGetProperty("matchesStorageClass", out var _) && r.GetProperty("condition").GetProperty("age").GetInt32() == 30).ShouldBeTrue();
    }

    [Fact]
    public async Task An_upload_of_a_stream_that_cannot_seek_gets_exactly_one_attempt()
    {
        var rig = NewRig();
        for (var i = 0; i < 3; i++)
        {
            rig.Handler.Returns(Post, UploadPath("deg-b"), 503, RpcError(503, "UNAVAILABLE", "try later"));
        }

        await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.UploadAsync("deg-b", "jobs/j1/manifest.json", new NonSeekableStream(Encoding.UTF8.GetBytes("abc")), CancellationToken.None));

        rig.Handler.To(Post, UploadPath("deg-b")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_upload_from_a_stream_positioned_part_way_sends_only_the_remaining_bytes_with_a_matching_range()
    {
        var rig = NewRig();
        ScriptUpload(rig.Handler, "deg-b");
        var content = new MemoryStream(Encoding.UTF8.GetBytes("0123456789")) { Position = 5 };

        await rig.Gateways.Storage.UploadAsync("deg-b", "jobs/j1/manifest.json", content, CancellationToken.None);

        var put = rig.Handler.To(Put, UploadPath("deg-b")).Single();
        put.Body.ShouldBe("56789");
        put.ContentRange.ShouldBe("bytes 0-4/5");
    }

    [Fact]
    public async Task A_bucket_on_the_second_page_of_the_listing_is_found_and_no_second_bucket_is_made()
    {
        var rig = NewRig();
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, "{\"kind\":\"storage#buckets\",\"items\":[" + BucketJson("deg-" + Number + "-zzzzzz", labelled: false) + "],\"nextPageToken\":\"page-2\"}");
        rig.Handler.Returns(Get, Buckets, 200, List(BucketJson(existing)));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(existing);

        rig.Handler.To(Post, Buckets).ShouldBeEmpty();
        rig.Handler.To(Get, Buckets).Last().Uri.Query.ShouldContain("pageToken=page-2");
    }

    [Theory]
    [InlineData("ubla")]
    [InlineData("pap")]
    public async Task An_adopted_bucket_with_drifted_access_settings_is_patched_with_the_iam_configuration(string drift)
    {
        var rig = NewRig();
        var existing = "deg-" + Number + "-abcdef";
        rig.Handler.Returns(Get, Buckets, 200, List(drift == "ubla" ? BucketJson(existing, ubla: false) : BucketJson(existing, pap: "inherited")));
        rig.Handler.Returns(Patch, BucketPath(existing), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(existing), 200, BucketJson(existing));
        ScriptConfigAlreadyThere(rig.Handler, existing);

        await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Patch, BucketPath(existing)).Single().Body);
        var iam = body.RootElement.GetProperty("iamConfiguration");
        iam.GetProperty("uniformBucketLevelAccess").GetProperty("enabled").GetBoolean().ShouldBeTrue();
        iam.GetProperty("publicAccessPrevention").GetString().ShouldBe("enforced");
    }

    [Fact]
    public async Task A_409_replay_whose_bucket_reads_back_drifted_is_repaired_in_the_same_call()
    {
        var rig = NewRig();
        var first = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 409, RpcError(409, "ALREADY_EXISTS", "You already own this bucket."));
        rig.Handler.Returns(Get, BucketPath(first), 200, BucketJson(first, ubla: false));
        rig.Handler.Returns(Patch, BucketPath(first), 200, "{}");
        rig.Handler.Returns(Get, BucketPath(first), 200, BucketJson(first));
        ScriptCreatedListing(rig.Handler, first);
        ScriptUpload(rig.Handler, first);

        (await rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None)).ShouldBe(first);

        rig.Handler.To(Patch, BucketPath(first)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("a\"b")]
    public async Task An_installation_id_that_is_not_a_valid_label_value_fails_before_any_request(string installationId)
    {
        var rig = NewRig(installationId: installationId);

        await Should.ThrowAsync<ArgumentException>(() => rig.Gateways.Storage.EnsureBucketAsync("my-lab", CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_domain_scoped_project_id_is_looked_up_not_refused_without_a_request()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Get, "/v3/projects/example.com%3Aproj", 200, ProjectBody());
        var name = Name(0);
        rig.Handler.Returns(Get, Buckets, 200, List());
        rig.Handler.Returns(Post, Buckets, 200, "{}");
        rig.Handler.Returns(Get, BucketPath(name), 200, BucketJson(name));
        ScriptCreatedListing(rig.Handler, name);
        ScriptUpload(rig.Handler, name);

        (await rig.Gateways.Storage.EnsureBucketAsync("example.com:proj", CancellationToken.None)).ShouldBe(name);
    }

    [Fact]
    public async Task A_project_id_with_a_slash_cannot_name_a_project_and_is_refused_without_a_request()
    {
        var rig = NewRig();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.EnsureBucketAsync("a/b", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.Requests.ShouldBeEmpty();
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    [Fact]
    public async Task Upload_sends_the_object_bytes_to_the_bucket_through_a_resumable_session()
    {
        var rig = NewRig();
        ScriptUpload(rig.Handler, "deg-b");

        await rig.Gateways.Storage.UploadAsync("deg-b", "jobs/j1/manifest.json", new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}")), CancellationToken.None);

        var start = rig.Handler.To(Post, UploadPath("deg-b")).Single();
        start.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        start.Body.ShouldContain("jobs/j1/manifest.json");
        rig.Handler.To(Put, UploadPath("deg-b")).Single().Body.ShouldBe("{\"a\":1}");
    }

    [Fact]
    public async Task Download_returns_the_object_bytes()
    {
        var rig = NewRig();
        rig.Handler.Returns(Get, ObjectPath, 200, "{\"ok\":true}");

        await using var stream = await rig.Gateways.Storage.DownloadAsync("deg-b", "jobs/j1/result.json", CancellationToken.None);

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldBe("{\"ok\":true}");
        rig.Handler.Requests.Single(r => r.Uri.Query.Contains("alt=media")).Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
    }

    [Fact]
    public async Task Download_of_a_missing_object_is_a_not_found_error()
    {
        var rig = NewRig();
        rig.Handler.Returns(Get, ObjectPath, 404, RpcError(404, "NOT_FOUND", "No such object"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Storage.DownloadAsync("deg-b", "jobs/j1/result.json", CancellationToken.None));

        ex.Error.HttpStatus.ShouldBe(404);
        ex.Error.Code.ShouldBe("NOT_FOUND");
    }

    [Fact]
    public async Task TryDownload_answers_null_for_a_404_and_throws_for_every_other_failure()
    {
        var rig = NewRig();
        rig.Handler.Returns(Get, ObjectPath, 404, RpcError(404, "NOT_FOUND", "No such object"));
        (await rig.Gateways.Storage.TryDownloadAsync("deg-b", "jobs/j1/result.json", CancellationToken.None)).ShouldBeNull();

        var denied = NewRig();
        denied.Handler.Returns(Get, ObjectPath, 403, RpcError(403, "PERMISSION_DENIED", "user@example.com does not have storage.objects.get access to the Google Cloud Storage object. Permission 'storage.objects.get' denied on resource (or it may not exist)."));
        var ex = await Should.ThrowAsync<CloudOperationException>(() => denied.Gateways.Storage.TryDownloadAsync("deg-b", "jobs/j1/result.json", CancellationToken.None));
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task TryDownload_returns_the_bytes_when_the_object_exists()
    {
        var rig = NewRig();
        rig.Handler.Returns(Get, ObjectPath, 200, "{\"done\":true}");

        await using var stream = (await rig.Gateways.Storage.TryDownloadAsync("deg-b", "jobs/j1/result.json", CancellationToken.None))!;

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldBe("{\"done\":true}");
    }
}
