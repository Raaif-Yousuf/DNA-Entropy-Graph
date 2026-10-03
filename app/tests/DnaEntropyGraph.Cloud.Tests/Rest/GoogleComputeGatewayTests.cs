using System.Text.Json;
using System.Web;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #56: the real Compute Engine v1 gateway over a scripted HTTP handler. Payloads are Google's documented REST
/// shapes (instances.insert returning a zone operation, zoneOperations.get, instances.get, aggregatedList, error
/// operations), not captured from a live project: there is no GCP access in this build (docs/ToTest.md carries the
/// row that needs a real VM). The decisive observable is the request itself.
/// </summary>
public class GoogleComputeGatewayTests
{
    private const string Zone = "us-central1-a";
    private const string Base = "/compute/v1/projects/my-lab";
    private const string Instances = Base + "/zones/" + Zone + "/instances";
    private const string Instance = Instances + "/deg-job1";
    private const string OperationPath = Base + "/zones/" + Zone + "/operations/operation-123";
    private const string Aggregated = Base + "/aggregated/instances";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Delete = HttpMethod.Delete;

    private static VmSpec Spec() => new(
        ProjectId: "my-lab",
        InstallationId: "install-1",
        JobId: "job1",
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromMinutes(90),
        TerminationAction: "DELETE")
    {
        Metadata = new Dictionary<string, string> { ["startup-script"] = "#!/usr/bin/env bash\necho hi\n", ["deg-job-id"] = "job1" },
    };

    private static string Op(string status, string kind = "insert", string? error = null, int? http = null)
        => "{\"kind\":\"compute#operation\",\"name\":\"operation-123\",\"operationType\":\"" + kind + "\",\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + Zone + "\",\"status\":\"" + status + "\""
            + (error is null ? string.Empty : ",\"error\":{\"errors\":[" + error + "]},\"httpErrorStatusCode\":" + http)
            + "}";

    private static string OpError(string code, string message) => "{\"code\":\"" + code + "\",\"message\":" + JsonSerializer.Serialize(message) + "}";

    private static string Vm(string name, string zone, string status, string job = "job1", string install = "install-1", string? stopped = null, string app = "dna-entropy-graph")
        => "{\"kind\":\"compute#instance\",\"name\":\"" + name + "\",\"status\":\"" + status + "\",\"statusMessage\":\"why\","
            + "\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + zone + "\",\"creationTimestamp\":\"2026-10-03T01:02:03.000-07:00\","
            + (stopped is null ? string.Empty : "\"lastStopTimestamp\":\"" + stopped + "\",")
            + "\"labels\":{\"app\":\"" + app + "\",\"job-id\":\"" + job + "\",\"installation-id\":\"" + install + "\"}}";

    private static string RpcError(int http, string status, string message, string? reason = null)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\""
            + (reason is null ? string.Empty : ",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"" + reason + "\"}]") + "}}";

    private static string Aggregate(string? next, params (string Zone, string[] Vms)[] zones)
        => "{\"kind\":\"compute#instanceAggregatedList\",\"items\":{"
            + string.Join(",", zones.Select(z => "\"zones/" + z.Zone + "\":" + (z.Vms.Length == 0
                ? "{\"warning\":{\"code\":\"NO_RESULTS_ON_PAGE\",\"message\":\"none\"}}"
                : "{\"instances\":[" + string.Join(",", z.Vms) + "]}")))
            + "}" + (next is null ? string.Empty : ",\"nextPageToken\":\"" + next + "\"") + "}";

    [Fact]
    public async Task Create_through_the_factory_sends_the_insert_to_compute_googleapis_with_every_label_and_the_lifetime_limits_in_the_body()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("PENDING"))
            .Returns(Get, OperationPath, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "PROVISIONING"));

        await rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None);

        var insert = rig.Handler.To(Post, Instances).Single();
        insert.Uri.Host.ShouldBe("compute.googleapis.com");
        insert.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        using var body = JsonDocument.Parse(insert.Body);
        var root = body.RootElement;
        root.GetProperty("name").GetString().ShouldBe("deg-job1");
        root.GetProperty("machineType").GetString().ShouldBe("zones/" + Zone + "/machineTypes/g2-standard-8");

        var labels = root.GetProperty("labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        labels.ShouldBe(new Dictionary<string, string?>
        {
            ["app"] = "dna-entropy-graph",
            ["installation-id"] = "install-1",
            ["job-id"] = "job1",
            ["model"] = "evo2_7b",
            ["app-version"] = "0-1-0",
            ["lifecycle"] = "stop",
        });

        var scheduling = root.GetProperty("scheduling");
        scheduling.GetProperty("maxRunDuration").GetProperty("seconds").GetInt64().ShouldBe(5400);
        scheduling.GetProperty("instanceTerminationAction").GetString().ShouldBe("DELETE");
        scheduling.GetProperty("onHostMaintenance").GetString().ShouldBe("TERMINATE");
        scheduling.GetProperty("automaticRestart").GetBoolean().ShouldBeFalse();

        var metadata = root.GetProperty("metadata").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("value").GetString());
        metadata["startup-script"].ShouldBe("#!/usr/bin/env bash\necho hi\n");
        metadata["deg-job-id"].ShouldBe("job1");
        metadata["block-project-ssh-keys"].ShouldBe("true");

        var disk = root.GetProperty("disks").EnumerateArray().Single();
        disk.GetProperty("boot").GetBoolean().ShouldBeTrue();
        disk.GetProperty("autoDelete").GetBoolean().ShouldBeTrue();
        disk.GetProperty("initializeParams").GetProperty("diskSizeGb").GetInt64().ShouldBe(150);
        disk.GetProperty("initializeParams").GetProperty("diskType").GetString().ShouldBe("zones/" + Zone + "/diskTypes/pd-balanced");
        disk.GetProperty("initializeParams").GetProperty("sourceImage").GetString().ShouldBe("projects/deeplearning-platform-release/global/images/family/pytorch-2-9-cu129-ubuntu-2404-nvidia-580");

        var account = root.GetProperty("serviceAccounts").EnumerateArray().Single();
        account.GetProperty("email").GetString().ShouldBe("deg-worker@my-lab.iam.gserviceaccount.com");
        account.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ShouldBe(["https://www.googleapis.com/auth/cloud-platform"]);
        root.GetProperty("networkInterfaces").EnumerateArray().Single().GetProperty("accessConfigs").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_cpu_machine_type_boots_container_optimized_os_not_the_gpu_image()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        await rig.Gateways.Compute.CreateVmAsync(Spec() with { MachineType = "e2-small" }, Zone, CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Post, Instances).Single().Body);
        body.RootElement.GetProperty("disks")[0].GetProperty("initializeParams").GetProperty("sourceImage").GetString()
            .ShouldBe("projects/cos-cloud/global/images/family/cos-stable");
        rig.Handler.To(Get, OperationPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_sends_a_request_id_that_is_the_same_on_every_replay_so_a_retried_insert_cannot_make_a_second_vm()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 503, RpcError(503, "UNAVAILABLE", "try later"))
            .Returns(Post, Instances, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        await rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None);

        var inserts = rig.Handler.To(Post, Instances);
        inserts.Count.ShouldBe(2);
        var ids = inserts.Select(i => HttpUtility.ParseQueryString(i.Uri.Query)["requestId"]).ToList();
        Guid.TryParse(ids[0], out _).ShouldBeTrue();
        ids[1].ShouldBe(ids[0]);
        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Create_polls_the_zone_operation_until_it_is_DONE_then_reads_the_instance()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("PENDING"))
            .Returns(Get, OperationPath, 200, Op("RUNNING"))
            .Returns(Get, OperationPath, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        var vm = await rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None);

        rig.Handler.To(Get, OperationPath).Count.ShouldBe(2);
        rig.Delays.ShouldBe([TimeSpan.FromSeconds(1)]);
        vm.Name.ShouldBe("deg-job1");
        vm.Zone.ShouldBe(Zone);
        vm.Status.ShouldBe("RUNNING");
        vm.CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 3, 1, 2, 3, TimeSpan.FromHours(-7)));
        vm.Labels!["job-id"].ShouldBe("job1");
    }

    public static IEnumerable<object[]> OperationFailures()
    {
        yield return new object[] { "ZONE_RESOURCE_POOL_EXHAUSTED", "The zone 'projects/my-lab/zones/us-central1-a' does not have enough resources available to fulfill the request.", 503, CloudErrorKind.Stockout };
        yield return new object[] { "QUOTA_EXCEEDED", "Quota 'NVIDIA_L4_GPUS' exceeded. Limit: 0.0 in region us-central1.", 403, CloudErrorKind.Quota };
        yield return new object[] { "RESOURCE_ALREADY_EXISTS", "The resource 'projects/my-lab/zones/us-central1-a/instances/deg-job1' already exists", 409, CloudErrorKind.AlreadyExists };
        yield return new object[] { "IAM_PERMISSION_DENIED", "Required 'compute.instances.create' permission for 'projects/my-lab/zones/us-central1-a/instances/deg-job1'", 403, CloudErrorKind.Permission };
    }

    [Theory]
    [MemberData(nameof(OperationFailures))]
    public async Task A_failed_zone_operation_surfaces_its_error_code_classified_not_a_generic_timeout(string code, string message, int http, CloudErrorKind expected)
    {
        OperationFailures().Count().ShouldBeGreaterThanOrEqualTo(4);
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("PENDING"))
            .Returns(Get, OperationPath, 200, Op("DONE", error: OpError(code, message), http: http));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe(code);
        ex.Error.HttpStatus.ShouldBe(http);
        ex.Kind.ShouldBe(expected);
        rig.Handler.To(Get, Instance).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_insert_already_DONE_with_an_error_is_surfaced_without_polling()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, Instances, 200, Op("DONE", error: OpError("ZONE_RESOURCE_POOL_EXHAUSTED", "no capacity"), http: 503));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Stockout);
        rig.Handler.To(Get, OperationPath).ShouldBeEmpty();
    }

    public static IEnumerable<object?[]> InsertRefusals()
    {
        yield return new object?[] { 403, "PERMISSION_DENIED", "Compute Engine API has not been used in project 123 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/compute.googleapis.com/overview then retry.", "SERVICE_DISABLED", CloudErrorKind.ApiDisabled };
        yield return new object?[] { 403, "PERMISSION_DENIED", "The billing account for the owning project is disabled in state closed", "BILLING_DISABLED", CloudErrorKind.Billing };
        yield return new object?[] { 409, "ALREADY_EXISTS", "The resource 'projects/my-lab/zones/us-central1-a/instances/deg-job1' already exists", null, CloudErrorKind.AlreadyExists };
        yield return new object?[] { 403, "PERMISSION_DENIED", "Required 'compute.instances.create' permission for 'projects/my-lab/zones/us-central1-a/instances/deg-job1'", null, CloudErrorKind.Permission };
    }

    [Theory]
    [MemberData(nameof(InsertRefusals))]
    public async Task A_refused_insert_is_classified_by_the_shared_error_rules(int http, string status, string message, string? reason, CloudErrorKind expected)
    {
        InsertRefusals().Count().ShouldBeGreaterThanOrEqualTo(4);
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, Instances, http, RpcError(http, status, message, reason));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(expected);
        rig.Handler.To(Post, Instances).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_spec_missing_a_label_never_reaches_the_network()
    {
        var rig = new GoogleGatewayHarness();

        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec() with { Model = string.Empty }, Zone, CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_operation_that_never_finishes_times_out_as_a_network_error_after_the_deadline()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromSeconds(5), retries: 0);
        rig.Handler.Returns(Post, Instances, 200, Op("PENDING"));
        for (var i = 0; i < 8; i++)
        {
            rig.Handler.Returns(Get, OperationPath, 200, Op("RUNNING"));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe(OperationPoller.TimeoutCode);
        ex.Kind.ShouldBe(CloudErrorKind.Network);
    }

    [Fact]
    public async Task Get_returns_the_descriptor_with_labels_and_the_last_stop_time_and_a_404_is_null()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "TERMINATED", stopped: "2026-10-03T04:05:06.000-07:00"))
            .Returns(Get, Instance, 404, RpcError(404, "NOT_FOUND", "The resource was not found"));

        var found = await rig.Gateways.Compute.GetVmAsync("deg-job1", Zone, CancellationToken.None);
        var gone = await rig.Gateways.Compute.GetVmAsync("deg-job1", Zone, CancellationToken.None);

        found!.Status.ShouldBe("TERMINATED");
        found.StatusReason.ShouldBe("why");
        found.StoppedAt.ShouldBe(new DateTimeOffset(2026, 10, 3, 4, 5, 6, TimeSpan.FromHours(-7)));
        gone.ShouldBeNull();
    }

    [Fact]
    public async Task A_transient_503_on_get_is_retried_by_the_pipeline()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Instance, 503, RpcError(503, "UNAVAILABLE", "try later"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        var vm = await rig.Gateways.Compute.GetVmAsync("deg-job1", Zone, CancellationToken.None);

        vm.ShouldNotBeNull();
        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stop_posts_to_the_stop_path_and_polls_its_operation()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instance + "/stop", 200, Op("PENDING", "stop"))
            .Returns(Get, OperationPath, 200, Op("DONE", "stop"));

        await rig.Gateways.Compute.StopVmAsync("deg-job1", Zone, CancellationToken.None);

        rig.Handler.To(Post, Instance + "/stop").Single().Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        rig.Handler.To(Get, OperationPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_sends_DELETE_on_the_instance_and_polls_its_operation()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Delete, Instance, 200, Op("PENDING", "delete"))
            .Returns(Get, OperationPath, 200, Op("DONE", "delete"));

        await rig.Gateways.Compute.DeleteVmAsync("deg-job1", Zone, CancellationToken.None);

        rig.Handler.To(Delete, Instance).Count.ShouldBe(1);
        rig.Handler.To(Get, OperationPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stop_and_delete_of_a_VM_that_is_already_gone_are_not_errors()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instance + "/stop", 404, RpcError(404, "NOT_FOUND", "not found"))
            .Returns(Delete, Instance, 404, RpcError(404, "NOT_FOUND", "not found"))
            .Returns(Delete, Instance, 200, Op("PENDING", "delete"))
            .Returns(Get, OperationPath, 200, Op("DONE", "delete", error: OpError("RESOURCE_NOT_FOUND", "The resource was not found"), http: 404));

        await rig.Gateways.Compute.StopVmAsync("deg-job1", Zone, CancellationToken.None);
        await rig.Gateways.Compute.DeleteVmAsync("deg-job1", Zone, CancellationToken.None);
        await rig.Gateways.Compute.DeleteVmAsync("deg-job1", Zone, CancellationToken.None);

        rig.Handler.To(Delete, Instance).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_delete_whose_operation_fails_for_another_reason_still_throws()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Delete, Instance, 200, Op("PENDING", "delete"))
            .Returns(Get, OperationPath, 200, Op("DONE", "delete", error: OpError("IAM_PERMISSION_DENIED", "Required 'compute.instances.delete' permission"), http: 403));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.DeleteVmAsync("deg-job1", Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task FindByJobId_asks_every_zone_for_the_job_label_and_follows_the_page_token()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Aggregated, 200, Aggregate("page2", ("us-central1-a", [Vm("deg-job1", "us-central1-a", "RUNNING")]), ("us-east1-b", [])))
            .Returns(Get, Aggregated, 200, Aggregate(null, ("europe-west4-a", [Vm("deg-job1", "europe-west4-a", "TERMINATED", stopped: "2026-10-03T04:05:06.000+00:00")])));

        var found = await rig.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None);

        found.Select(v => (v.Name, v.Zone)).ShouldBe([("deg-job1", "us-central1-a"), ("deg-job1", "europe-west4-a")]);
        found[1].StoppedAt.ShouldBe(new DateTimeOffset(2026, 10, 3, 4, 5, 6, TimeSpan.Zero));
        var calls = rig.Handler.To(Get, Aggregated);
        calls.Count.ShouldBe(2);
        var first = HttpUtility.ParseQueryString(calls[0].Uri.Query);
        first["filter"].ShouldNotBeNull().ShouldContain("labels.job-id = \"job1\"");
        first["filter"]!.ShouldContain("labels.app = \"dna-entropy-graph\"");
        first["pageToken"].ShouldBeNull();
        HttpUtility.ParseQueryString(calls[1].Uri.Query)["pageToken"].ShouldBe("page2");
        HttpUtility.ParseQueryString(calls[1].Uri.Query)["filter"].ShouldBe(first["filter"]);
    }

    [Fact]
    public async Task FindByJobId_drops_an_instance_that_does_not_carry_the_job_label_even_if_the_filter_let_it_through()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, Aggregated, 200, Aggregate(null, ("us-central1-a", [Vm("deg-job1", "us-central1-a", "RUNNING"), Vm("deg-other", "us-central1-a", "RUNNING", job: "other"), Vm("deg-job1", "us-central1-b", "RUNNING", app: "someone-else")])));

        var found = await rig.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None);

        found.Select(v => v.Name).ShouldBe(["deg-job1"]);
    }

    [Fact]
    public async Task FindByJobId_with_an_id_no_label_can_hold_returns_nothing_and_asks_nobody()
    {
        var rig = new GoogleGatewayHarness();

        var found = await rig.Gateways.Compute.FindByJobIdAsync("a\" OR labels.app:*", CancellationToken.None);

        found.ShouldBeEmpty();
        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListByInstallation_filters_on_the_installation_label_and_fills_labels_and_stop_time()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, Aggregated, 200, Aggregate(
            null,
            ("us-central1-a", [Vm("deg-a", "us-central1-a", "TERMINATED", job: "a", stopped: "2026-10-03T04:05:06.000-07:00")]),
            ("us-west1-b", [Vm("deg-b", "us-west1-b", "RUNNING", job: "b")])));

        var found = await rig.Gateways.Compute.ListByInstallationAsync("install-1", CancellationToken.None);

        var filter = HttpUtility.ParseQueryString(rig.Handler.To(Get, Aggregated).Single().Uri.Query)["filter"];
        filter.ShouldNotBeNull().ShouldContain("labels.installation-id = \"install-1\"");
        filter.ShouldContain("labels.app = \"dna-entropy-graph\"");
        found.Count.ShouldBe(2);
        found[0].Labels!["job-id"].ShouldBe("a");
        found[0].StoppedAt.ShouldBe(new DateTimeOffset(2026, 10, 3, 4, 5, 6, TimeSpan.FromHours(-7)));
        found[1].StoppedAt.ShouldBeNull();
        found[1].Zone.ShouldBe("us-west1-b");
    }

    [Fact]
    public async Task Calls_that_need_a_project_fail_loudly_when_none_is_selected()
    {
        var rig = new GoogleGatewayHarness(selectedProjectId: null);

        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Compute.GetVmAsync("deg-job1", Zone, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Compute.ListByInstallationAsync("install-1", CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }
}
