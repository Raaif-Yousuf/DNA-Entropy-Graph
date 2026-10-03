using System.Text.Json;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Cloud.Tests.Rest;
using DnaEntropyGraph.CloudCli;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.CloudCli;

/// <summary>
/// Issue #60, the wired-to-nothing observable: each verb run through the same entry point the console uses
/// (<see cref="CloudCliApp"/> over <see cref="GoogleCloudCliSessionFactory"/>) reaches the REAL gateways, which talk HTTP to Google's
/// Compute and Storage endpoints with the signed-in account's bearer token. A host that fell back to <see cref="FakeGcp"/> would
/// print its own invented rows and send no request, so every test asserts on the recorded requests, not only on the output. The
/// payloads are Google's documented REST shapes, not captured from a live project (no GCP access in this build).
/// </summary>
public class CloudCliEndToEndTests
{
    private const string Zone = "us-central1-a";
    private const string Base = "/compute/v1/projects/my-lab";
    private const string Aggregated = Base + "/aggregated/instances";
    private const string Instances = Base + "/zones/" + Zone + "/instances";
    private const string OperationPath = Base + "/zones/" + Zone + "/operations/operation-123";
    private const string Buckets = "/storage/v1/b";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Delete = HttpMethod.Delete;

    private sealed record Outcome(int ExitCode, string Out, string Err);

    private sealed class Rig
    {
        public Rig(bool signedIn = true)
        {
            var tokens = new StubTokenSource();
            Tokens = tokens;
            Handler = new ScriptedHttpHandler();
            Sessions = new GoogleCloudCliSessionFactory(() => new CloudCliAuth(signedIn, tokens, tokens), Handler, instant: true);
        }

        public StubTokenSource Tokens { get; }

        public ScriptedHttpHandler Handler { get; }

        public GoogleCloudCliSessionFactory Sessions { get; }

        public async Task<Outcome> RunAsync(params string[] args)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await new CloudCliApp(Sessions, TimeProvider.System, new Random(60)).RunAsync(args, stdout, stderr, CancellationToken.None);
            return new Outcome(code, stdout.ToString(), stderr.ToString());
        }
    }

    private static string Op(string status, string kind = "insert")
        => "{\"kind\":\"compute#operation\",\"name\":\"operation-123\",\"operationType\":\"" + kind + "\",\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + Zone + "\",\"status\":\"" + status + "\"}";

    private static string Vm(string name, string zone, string status, string job = "job1", string install = "inst-1", string app = "dna-entropy-graph")
        => "{\"kind\":\"compute#instance\",\"name\":\"" + name + "\",\"status\":\"" + status + "\","
            + "\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + zone + "\",\"creationTimestamp\":\"2026-10-03T01:02:03.000-07:00\","
            + "\"labels\":{\"app\":\"" + app + "\",\"job-id\":\"" + job + "\",\"installation-id\":\"" + install + "\"}}";

    private static string Aggregate(params string[] vms)
        => "{\"kind\":\"compute#instanceAggregatedList\",\"items\":{\"zones/" + Zone + "\":{\"instances\":[" + string.Join(",", vms) + "]}}}";

    private static string Bucket(string name, string install = "inst-1", string app = "dna-entropy-graph")
        => "{\"kind\":\"storage#bucket\",\"name\":\"" + name + "\",\"location\":\"US\",\"timeCreated\":\"2026-10-01T10:00:00.000Z\","
            + "\"labels\":{\"app\":\"" + app + "\",\"installation-id\":\"" + install + "\",\"lifecycle\":\"results\"}}";

    private static string BucketList(params string[] buckets) => "{\"kind\":\"storage#buckets\",\"items\":[" + string.Join(",", buckets) + "]}";

    [Fact]
    public async Task Resources_list_prints_the_labelled_vm_and_bucket_the_real_gateways_read_over_http_with_the_account_token()
    {
        var rig = new Rig();
        rig.Handler
            .Returns(Get, Aggregated, 200, Aggregate(Vm("deg-job1", Zone, "RUNNING")))
            .Returns(Get, Buckets, 200, BucketList(Bucket("deg-123-abcdef"), Bucket("deg-456-zzzzzz", app: "someone-else")));

        var result = await rig.RunAsync("resources", "list", "--project", "my-lab");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("deg-job1");
        result.Out.ShouldContain(Zone);
        result.Out.ShouldContain("deg-123-abcdef");
        result.Out.ShouldNotContain("deg-456-zzzzzz");
        result.Out.ShouldContain("OK: 2 resource(s)");

        var compute = rig.Handler.To(Get, Aggregated).Single();
        compute.Uri.Host.ShouldBe("compute.googleapis.com");
        compute.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        compute.Uri.Query.ShouldContain("dna-entropy-graph");
        var storage = rig.Handler.To(Get, Buckets).Single();
        storage.Uri.Host.ShouldBe("storage.googleapis.com");
        storage.Uri.Query.ShouldContain("project=my-lab");
        storage.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
    }

    [Fact]
    public async Task Resources_list_with_install_filters_the_vm_query_but_keeps_the_shared_bucket()
    {
        var rig = new Rig();
        rig.Handler
            .Returns(Get, Aggregated, 200, Aggregate(Vm("deg-job1", Zone, "RUNNING")))
            .Returns(Get, Buckets, 200, BucketList(Bucket("deg-123-abcdef", install: "inst-2")));

        var result = await rig.RunAsync("resources", "list", "--project", "my-lab", "--install", "inst-1", "--json");

        using var document = JsonDocument.Parse(result.Out);
        document.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ShouldBe(["deg-job1", "deg-123-abcdef"]);
        Uri.UnescapeDataString(rig.Handler.To(Get, Aggregated).Single().Uri.Query).ShouldContain("labels.installation-id = \"inst-1\"");
    }

    [Fact]
    public async Task Resources_list_after_a_clean_run_is_an_empty_array_for_the_leak_scan()
    {
        var rig = new Rig();
        rig.Handler.Returns(Get, Aggregated, 200, "{\"kind\":\"compute#instanceAggregatedList\",\"items\":{}}");

        var result = await rig.RunAsync("resources", "list", "--project", "my-lab", "--install", "inst-1", "--kind", "vm", "--json");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.Trim().ShouldBe("[]");
        rig.Handler.To(Get, Buckets).ShouldBeEmpty();
    }

    [Fact]
    public async Task Bucket_describe_prints_the_bucket_the_storage_api_returned()
    {
        var rig = new Rig();
        rig.Handler.Returns(Get, Buckets, 200, BucketList(Bucket("deg-123-abcdef")));

        var result = await rig.RunAsync("bucket", "describe", "--project", "my-lab", "--name", "deg-123-abcdef");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("name: deg-123-abcdef");
        result.Out.ShouldContain("location: US");
        result.Out.ShouldContain("installation-id=inst-1");
    }

    [Fact]
    public async Task Vm_create_sends_the_insert_with_every_label_the_max_run_duration_and_delete_termination()
    {
        var rig = new Rig();
        rig.Handler
            .Returns(Post, Instances, 200, Op("PENDING"))
            .Returns(Get, OperationPath, 200, Op("DONE"))
            .Returns(Get, Instances + "/deg-job9", 200, Vm("deg-job9", Zone, "PROVISIONING", job: "job9"));

        var result = await rig.RunAsync(
            "vm", "create", "--project", "my-lab", "--zone", Zone, "--install", "inst-1", "--job", "job9",
            "--service-account", "w@my-lab.iam.gserviceaccount.com", "--max-run-duration", "20m");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("OK: created deg-job9");
        var insert = rig.Handler.To(Post, Instances).Single();
        insert.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        using var body = JsonDocument.Parse(insert.Body);
        var root = body.RootElement;
        root.GetProperty("name").GetString().ShouldBe("deg-job9");
        var labels = root.GetProperty("labels").EnumerateObject().Select(p => p.Name).ToList();
        labels.ShouldBe(["app", "installation-id", "job-id", "model", "app-version", "lifecycle"], ignoreOrder: true);
        var scheduling = root.GetProperty("scheduling");
        scheduling.GetProperty("instanceTerminationAction").GetString().ShouldBe("DELETE");
        scheduling.GetProperty("maxRunDuration").GetProperty("seconds").GetInt64().ShouldBe(1200);
    }

    [Fact]
    public async Task Vm_delete_reads_the_vm_checks_its_label_then_sends_the_delete()
    {
        var rig = new Rig();
        rig.Handler
            .Returns(Get, Instances + "/deg-job1", 200, Vm("deg-job1", Zone, "RUNNING"))
            .Returns(Delete, Instances + "/deg-job1", 200, Op("PENDING", "delete"))
            .Returns(Get, OperationPath, 200, Op("DONE", "delete"));

        var result = await rig.RunAsync("vm", "delete", "--project", "my-lab", "--zone", Zone, "--name", "deg-job1");

        result.ExitCode.ShouldBe(CloudCliExitCode.Ok);
        result.Out.ShouldContain("OK: deleted deg-job1");
        rig.Handler.To(Delete, Instances + "/deg-job1").Single().Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
    }

    [Fact]
    public async Task Vm_delete_of_a_vm_without_the_app_label_sends_no_delete_request()
    {
        var rig = new Rig();
        rig.Handler.Returns(Get, Instances + "/deg-job1", 200, Vm("deg-job1", Zone, "RUNNING", app: "someone-else"));

        var result = await rig.RunAsync("vm", "delete", "--project", "my-lab", "--zone", Zone, "--name", "deg-job1");

        result.ExitCode.ShouldBe(CloudCliExitCode.Refused);
        rig.Handler.Requests.ShouldNotContain(r => r.Method == Delete);
    }

    [Fact]
    public async Task Signed_out_stops_before_any_request_leaves_the_process()
    {
        var rig = new Rig(signedIn: false);

        var result = await rig.RunAsync("resources", "list", "--project", "my-lab");

        result.ExitCode.ShouldBe(CloudCliExitCode.NotSignedIn);
        rig.Handler.Requests.ShouldBeEmpty();
        rig.Tokens.TokenReads.ShouldBe(0);
    }

    [Fact]
    public async Task The_session_the_console_uses_holds_the_real_gateways_and_not_FakeGcp()
    {
        var rig = new Rig();

        var session = rig.Sessions.Open("my-lab", "inst-1");

        session.Compute.GetType().Name.ShouldBe("GoogleComputeGateway"); // internal type; resilience is per request inside it (#56)
        session.Compute.ShouldNotBeOfType<ResilientComputeGateway>();
        session.Compute.ShouldNotBeOfType<FakeGcp>();
        session.Storage.ShouldNotBeOfType<FakeGcp>();
        session.Storage.ShouldNotBeOfType<ResilientStorageGateway>();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_production_console_host_with_no_signed_in_account_exits_not_signed_in_over_the_real_account_store()
    {
        var empty = Path.Combine(Path.GetTempPath(), "deg-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var code = await CloudCliHost.RunAsync(["resources", "list", "--project", "my-lab"], stdout, stderr, empty, CancellationToken.None);

            code.ShouldBe(CloudCliExitCode.NotSignedIn);
            stderr.ToString().ShouldContain("Sign in with the app first");
            stdout.ToString().ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(empty))
            {
                Directory.Delete(empty, recursive: true);
            }
        }
    }
}
