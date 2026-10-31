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
        ServiceAccountEmail = "worker-sa@my-lab.iam.gserviceaccount.com",
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
        disk.GetProperty("initializeParams").GetProperty("labels").EnumerateObject().ToDictionary(l => l.Name, l => l.Value.GetString()).ShouldBe(labels);
        disk.GetProperty("initializeParams").GetProperty("diskType").GetString().ShouldBe("zones/" + Zone + "/diskTypes/pd-balanced");
        disk.GetProperty("initializeParams").GetProperty("sourceImage").GetString().ShouldBe("projects/deeplearning-platform-release/global/images/family/pytorch-2-9-cu129-ubuntu-2404-nvidia-580");

        var account = root.GetProperty("serviceAccounts").EnumerateArray().Single();
        account.GetProperty("email").GetString().ShouldBe("worker-sa@my-lab.iam.gserviceaccount.com");
        account.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ShouldBe(["https://www.googleapis.com/auth/cloud-platform"]);
        root.GetProperty("networkInterfaces").EnumerateArray().Single().GetProperty("accessConfigs").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_cpu_machine_type_boots_the_same_deep_learning_image_family_because_startup_sh_needs_docker_python3_and_a_writable_root()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        await rig.Gateways.Compute.CreateVmAsync(Spec() with { MachineType = "e2-small" }, Zone, CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Post, Instances).Single().Body);
        body.RootElement.GetProperty("disks")[0].GetProperty("initializeParams").GetProperty("sourceImage").GetString()
            .ShouldBe("projects/deeplearning-platform-release/global/images/family/pytorch-2-9-cu129-ubuntu-2404-nvidia-580");
        rig.Handler.To(Get, OperationPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_sends_a_fresh_request_id_on_every_call_so_a_replay_never_returns_an_old_operation()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"))
            .Returns(Post, Instances, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        await rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None);
        await rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None);

        var ids = rig.Handler.To(Post, Instances).Select(i => HttpUtility.ParseQueryString(i.Uri.Query)["requestId"]).ToList();
        ids.Count.ShouldBe(2);
        ids.ForEach(id => Guid.TryParse(id, out _).ShouldBeTrue());
        ids[1].ShouldNotBe(ids[0]);
    }

    [Fact]
    public async Task A_transient_error_while_polling_retries_that_poll_and_never_replays_the_insert()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("PENDING"))
            .Returns(Get, OperationPath, 503, RpcError(503, "UNAVAILABLE", "try later"))
            .Returns(Get, OperationPath, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        await rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None);

        rig.Handler.To(Post, Instances).Count.ShouldBe(1);
        rig.Handler.To(Get, OperationPath).Count.ShouldBe(2);
        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_insert_replayed_after_a_503_carries_the_same_request_id()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 503, RpcError(503, "UNAVAILABLE", "try later"))
            .Returns(Post, Instances, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "RUNNING"));

        await rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None);

        var ids = rig.Handler.To(Post, Instances).Select(i => HttpUtility.ParseQueryString(i.Uri.Query)["requestId"]).ToList();
        ids.Count.ShouldBe(2);
        Guid.TryParse(ids[0], out _).ShouldBeTrue();
        ids[1].ShouldBe(ids[0]);
    }

    [Fact]
    public async Task A_create_retried_after_the_first_insert_made_the_vm_surfaces_already_exists_so_the_runner_adopts_instead_of_creating_twice()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 409, RpcError(409, "ALREADY_EXISTS", "The resource 'projects/my-lab/zones/us-central1-a/instances/deg-job1' already exists"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
        rig.Handler.To(Post, Instances).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_create_for_a_project_other_than_the_selected_one_is_refused_by_name_before_any_request()
    {
        var rig = new GoogleGatewayHarness();

        // Issue #830: the scope the call is made in (not whatever is selected) must name the spec's project.
        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(new CloudScope("another-project"), Spec(), Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe("PROJECT_MISMATCH");
        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_create_in_a_scope_with_no_project_is_refused_before_any_request()
    {
        var rig = new GoogleGatewayHarness();

        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Compute.CreateVmAsync(new CloudScope(string.Empty), Spec(), Zone, CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_create_without_a_worker_service_account_is_refused_by_name_before_any_request()
    {
        var rig = new GoogleGatewayHarness();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec() with { ServiceAccountEmail = null }, Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe("WORKER_SERVICE_ACCOUNT_MISSING");
        ex.Kind.ShouldBe(CloudErrorKind.Other);
        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_vm_reported_done_but_not_found_is_a_network_class_unknown_outcome_not_other()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler
            .Returns(Post, Instances, 200, Op("DONE"))
            .Returns(Get, Instance, 404, RpcError(404, "NOT_FOUND", "The resource was not found"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        ex.Error.Code.ShouldBe("CREATE_OUTCOME_UNKNOWN");
    }

    [Fact]
    public async Task An_operation_with_a_generic_first_error_and_a_stockout_second_is_classified_stockout()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("DONE", error: OpError("INTERNAL_ERROR", "something went wrong") + "," + OpError("ZONE_RESOURCE_POOL_EXHAUSTED", "no capacity"), http: 503));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(Spec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Stockout);
        ex.Error.Code.ShouldBe("ZONE_RESOURCE_POOL_EXHAUSTED");
    }

    [Fact]
    public async Task FindByJobId_whose_aggregated_list_reports_an_unreachable_zone_throws_a_retryable_error_instead_of_an_empty_list()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Get, Aggregated, 200, "{\"kind\":\"compute#instanceAggregatedList\",\"items\":{},\"unreachables\":[\"zones/us-central1-a\"]}");

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        ex.Error.Code.ShouldBe("ZONES_UNREACHABLE");
        ex.Error.Message.ShouldContain("us-central1-a");
    }

    [Fact]
    public async Task ListByInstallation_with_unreachable_zones_beside_found_vms_still_throws_never_a_partial_list()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Get, Aggregated, 200, "{\"items\":{\"zones/us-east1-b\":{\"instances\":[" + Vm("deg-a", "us-east1-b", "RUNNING", job: "a") + "]}},\"unreachables\":[\"zones/us-central1-a\"]}");

        await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ListByInstallationAsync("install-1", CancellationToken.None));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task End_vm_reports_acceptance_when_the_insert_is_accepted_before_the_operation_is_polled(bool delete)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(delete ? Delete : Post, delete ? Instance : Instance + "/stop", 200, Op("PENDING", delete ? "delete" : "stop"))
            .Returns(Get, OperationPath, 200, Op("DONE", delete ? "delete" : "stop"));
        var pollsWhenAccepted = -1;
        var requestsWhenAccepted = -1;

        await rig.Gateways.Compute.EndVmAsync("deg-job1", Zone, delete, () =>
        {
            pollsWhenAccepted = rig.Handler.To(Get, OperationPath).Count;
            requestsWhenAccepted = rig.Handler.To(delete ? Delete : Post, delete ? Instance : Instance + "/stop").Count;
        }, CancellationToken.None);

        requestsWhenAccepted.ShouldBe(1);
        pollsWhenAccepted.ShouldBe(0); // told BEFORE the slow part: the polling that waits for DONE
        rig.Handler.To(Get, OperationPath).Count.ShouldBe(1);
    }

    [Fact]
    public async Task End_vm_does_not_report_acceptance_when_the_insert_is_refused()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, Instance + "/stop", 403, RpcError(403, "PERMISSION_DENIED", "denied"));
        var accepted = false;

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.EndVmAsync("deg-job1", Zone, false, () => accepted = true, CancellationToken.None));

        ex.Error.HttpStatus.ShouldBe(403);
        accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task End_vm_of_a_vm_that_is_already_gone_reports_acceptance_and_is_not_an_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Delete, Instance, 404, RpcError(404, "NOT_FOUND", "not found"));
        var accepted = false;

        await rig.Gateways.Compute.EndVmAsync("deg-job1", Zone, true, () => accepted = true, CancellationToken.None);

        accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task An_operation_that_fails_after_acceptance_is_reported_accepted_and_then_throws()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instance + "/stop", 200, Op("PENDING", "stop"))
            .Returns(Get, OperationPath, 200, Op("DONE", "stop", error: OpError("IAM_PERMISSION_DENIED", "Required permission"), http: 403));
        var accepted = false;

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.EndVmAsync("deg-job1", Zone, false, () => accepted = true, CancellationToken.None));

        accepted.ShouldBeTrue();
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
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
    public async Task The_aggregated_list_asks_for_partial_success_so_unreachable_zones_are_reported_not_hidden()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, Aggregated, 200, Aggregate(null, ("us-central1-a", [])));

        await rig.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None);

        HttpUtility.ParseQueryString(rig.Handler.To(Get, Aggregated).Single().Uri.Query)["returnPartialSuccess"].ShouldBe("true");
    }

    [Fact]
    public async Task A_repeated_page_token_fails_the_listing_instead_of_silently_truncating_it()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler
            .Returns(Get, Aggregated, 200, Aggregate("same", ("us-central1-a", [Vm("deg-job1", "us-central1-a", "RUNNING")])))
            .Returns(Get, Aggregated, 200, Aggregate("same", ("us-east1-b", [])));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.FindByJobIdAsync("job1", CancellationToken.None));

        ex.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        ex.Kind.ShouldBe(CloudErrorKind.Network, "issue #933 DECISION: a stuck listing is kind Network (retried, deferred by the reconciler); see docs/cloud_design.md, Paging");
    }

    [Fact]
    public async Task FindByJobId_with_a_trailing_newline_in_the_id_asks_nobody()
    {
        var rig = new GoogleGatewayHarness();

        var found = await rig.Gateways.Compute.FindByJobIdAsync("job1\n", CancellationToken.None);

        found.ShouldBeEmpty();
        rig.Handler.Requests.ShouldBeEmpty();
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

    // ---- #670: the accelerator offer read ----

    private const string AcceleratorAggregated = Base + "/aggregated/acceleratorTypes";

    [Fact]
    public async Task The_accelerator_offer_read_filters_by_name_follows_the_page_token_and_answers_the_zones_that_list_it()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, AcceleratorAggregated, 200,
                "{\"items\":{\"zones/us-central1-a\":{\"acceleratorTypes\":[{\"name\":\"nvidia-l4\"}]},\"zones/us-central1-b\":{\"warning\":{\"code\":\"NO_RESULTS_ON_PAGE\",\"message\":\"none\"}}},\"nextPageToken\":\"p2\"}")
            .Returns(Get, AcceleratorAggregated, 200,
                "{\"items\":{\"zones/europe-west4-a\":{\"acceleratorTypes\":[{\"name\":\"nvidia-l4\"}]}}}");

        var zones = await rig.Gateways.Compute.ListAcceleratorZonesAsync("nvidia-l4", CancellationToken.None);

        zones.ShouldBe(["us-central1-a", "europe-west4-a"]);
        var requests = rig.Handler.To(Get, AcceleratorAggregated);
        requests.Count.ShouldBe(2);
        HttpUtility.UrlDecode(requests[0].Uri.Query).ShouldContain("filter=name = \"nvidia-l4\"");
        requests[0].Uri.Query.ShouldContain("returnPartialSuccess=true");
        requests[1].Uri.Query.ShouldContain("pageToken=p2");
    }

    [Fact]
    public async Task An_accelerator_name_that_could_carry_filter_syntax_asks_nobody()
    {
        var rig = new GoogleGatewayHarness();

        (await rig.Gateways.Compute.ListAcceleratorZonesAsync("nvidia-l4\" OR name = \"x", CancellationToken.None)).ShouldBeEmpty();

        rig.Handler.Requests.ShouldBeEmpty();
    }

    // ---- #94: reuse of a stopped VM ----

    private const string OldInstance = Instances + "/deg-job-old";

    private static string StoppedOld(string status = "TERMINATED")
        => "{\"kind\":\"compute#instance\",\"name\":\"deg-job-old\",\"status\":\"" + status + "\","
            + "\"zone\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + Zone + "\",\"creationTimestamp\":\"2026-10-03T01:02:03.000-07:00\","
            + "\"machineType\":\"https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + Zone + "/machineTypes/g2-standard-8\","
            + "\"labelFingerprint\":\"lf1\",\"disks\":[{\"boot\":true,\"source\":\"" + DiskSource + "\"}],\"metadata\":{\"fingerprint\":\"mf1\",\"items\":[{\"key\":\"deg-job-id\",\"value\":\"job-old\"}]},"
            + "\"labels\":{\"app\":\"dna-entropy-graph\",\"job-id\":\"job-old\",\"installation-id\":\"install-1\"}}";

    private const string OldDisk = Base + "/zones/" + Zone + "/disks/deg-job-old";

    private const string DiskSource = "https://www.googleapis.com/compute/v1/projects/my-lab/zones/" + Zone + "/disks/deg-job-old";

    private static string OldDiskBody()
        => "{\"kind\":\"compute#disk\",\"name\":\"deg-job-old\",\"labelFingerprint\":\"df1\",\"labels\":{\"app\":\"dna-entropy-graph\",\"job-id\":\"job-old\",\"installation-id\":\"install-1\"}}";

    // Issue #476: the fence before the app ends a kept VM.
    [Fact]
    public async Task MarkEnding_writes_the_ending_label_with_the_fingerprint_it_read_and_keeps_the_other_labels()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Get, OldInstance, 200, StoppedOld());

        var vm = await rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-old", CancellationToken.None);

        vm.ShouldNotBeNull();
        using var body = JsonDocument.Parse(rig.Handler.To(Post, OldInstance + "/setLabels").Single().Body);
        body.RootElement.GetProperty("labelFingerprint").GetString().ShouldBe("lf1", "the write must fail if another run relabelled the VM since the read");
        var sent = body.RootElement.GetProperty("labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        sent["job-id"].ShouldBe("job-old");
        sent["installation-id"].ShouldBe("install-1");
        sent.ShouldContainKey(VmEnding.LabelKey);
    }

    [Fact]
    public async Task MarkEnding_that_loses_the_fingerprint_race_returns_null()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "Labels fingerprint either invalid or resource labels have changed", "conditionNotMet"));

        (await rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-old", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task MarkEnding_whose_polled_operation_fails_with_the_gone_code_returns_null_not_an_exception()
    {
        // Round 3 low finding: the 404 can surface inside the POLLED operation, under its own code and with no http status of its own.
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels", error: OpError("RESOURCE_NOT_FOUND", "the write did not apply"), http: 400));

        (await rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-old", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task MarkEnding_whose_polled_operation_fails_with_a_bare_condition_not_met_and_no_412_throws_as_org_policy()
    {
        // Round 5 finding 2: CloudErrorClassifier calls a CONDITION_NOT_MET with no HTTP 412 an org-policy refusal; the gateway used to read it as a lost race and the sweep retried silently for ever.
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels", error: OpError("CONDITION_NOT_MET", "Operation denied"), http: 400));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-old", CancellationToken.None));

        thrown.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
        thrown.Error.Code.ShouldBe("CONDITION_NOT_MET");
    }

    [Fact]
    public async Task MarkEnding_refused_by_an_org_policy_with_a_412_throws_as_org_policy_and_is_not_read_as_a_lost_race()
    {
        // Round 4 finding 2: any 412 used to mean "the labels moved", which is also the shape of an org-policy denial.
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "Operation denied by org policy on 'constraints/compute.requireOsLogin'."));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-old", CancellationToken.None));

        thrown.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
    }

    [Fact]
    public async Task MarkEnding_whose_polled_operation_names_an_org_policy_throws_as_org_policy()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels", error: OpError("CONDITION_NOT_MET", "Constraint constraints/compute.requireOsLogin violated"), http: 400));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-old", CancellationToken.None));

        thrown.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
    }

    [Fact]
    public async Task MarkEnding_whose_polled_operation_fails_otherwise_still_throws()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels", error: OpError("QUOTA_EXCEEDED", "no"), http: 403));

        await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-old", CancellationToken.None));
    }

    [Fact]
    public async Task MarkEnding_for_a_VM_another_job_now_owns_writes_nothing()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 200, StoppedOld());

        (await rig.Gateways.Compute.MarkEndingAsync("deg-job-old", Zone, "job-NEW", CancellationToken.None)).ShouldBeNull();

        rig.Handler.Requests.Any(r => r.Method == Post).ShouldBeFalse();
    }

    [Fact]
    public async Task Reuse_refuses_a_VM_the_app_marked_for_ending_and_writes_nothing()
    {
        var rig = new GoogleGatewayHarness();
        var marked = StoppedOld().Replace("\"installation-id\":\"install-1\"}", "\"installation-id\":\"install-1\",\"" + VmEnding.LabelKey + "\":\"" + VmEnding.Stamp(DateTimeOffset.UtcNow) + "\"}", StringComparison.Ordinal);
        rig.Handler.Returns(Get, OldInstance, 200, marked);

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.Code.ShouldBe("VM_ENDING");
        rig.Handler.Requests.Any(r => r.Method == Post).ShouldBeFalse("no claim, no start");
    }

    // Issue #678. FakeGcp does not model disks, so this gateway test (the request itself) is the only proof.
    [Fact]
    public async Task Reuse_rewrites_the_boot_disk_labels_to_the_new_job_after_the_claim_and_before_the_start()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setScheduling", 200, Op("DONE", "setScheduling"))
            .Returns(Post, OldInstance + "/setMetadata", 200, Op("DONE", "setMetadata"))
            .Returns(Post, OldInstance + "/start", 200, Op("DONE", "start"))
            .Returns(Get, OldInstance, 200, Vm("deg-job-old", Zone, "RUNNING", job: "job1"));

        await rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None);

        rig.Handler.Requests.Where(r => r.Method == Post).Select(r => r.Uri.AbsolutePath[r.Uri.AbsolutePath.IndexOf("/zones/", StringComparison.Ordinal)..])
            .ShouldBe(
                [$"/zones/{Zone}/instances/deg-job-old/setLabels", $"/zones/{Zone}/disks/deg-job-old/setLabels", $"/zones/{Zone}/instances/deg-job-old/setScheduling", $"/zones/{Zone}/instances/deg-job-old/setMetadata", $"/zones/{Zone}/instances/deg-job-old/start"],
                "the disk is relabelled after the instance claim and before anything else or the start");
        using var body = JsonDocument.Parse(rig.Handler.To(Post, OldDisk + "/setLabels").Single().Body);
        body.RootElement.GetProperty("labelFingerprint").GetString().ShouldBe("df1");
        var sent = body.RootElement.GetProperty("labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        sent["job-id"].ShouldBe("job1", "a reused VM's boot disk must not keep the old job's id");
        sent["app"].ShouldBe("dna-entropy-graph");
        sent.Count.ShouldBe(6);
    }

    [Fact]
    public async Task A_failed_disk_relabel_is_a_start_failure_and_the_VM_is_never_started()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 403, RpcError(403, "PERMISSION_DENIED", "denied", "forbidden"));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.HttpStatus.ShouldBe(403, "the disk's own refusal, not a later missing stub");
        rig.Handler.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/setScheduling", StringComparison.Ordinal)).ShouldBeFalse();
        rig.Handler.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task Reuse_claims_a_stopped_VM_by_labels_first_then_scheduling_then_metadata_then_start_each_with_its_fingerprint()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setScheduling", 200, Op("DONE", "setScheduling"))
            .Returns(Post, OldInstance + "/setMetadata", 200, Op("DONE", "setMetadata"))
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/start", 200, Op("DONE", "start"))
            .Returns(Get, OldInstance, 200, Vm("deg-job-old", Zone, "RUNNING", job: "job1"));

        var vm = await rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None);

        vm.Name.ShouldBe("deg-job-old");
        rig.Handler.Requests.Where(r => r.Method == Post).Select(r => r.Uri.AbsolutePath[(r.Uri.AbsolutePath.LastIndexOf('/') + 1)..])
            .ShouldBe(["setLabels", "setLabels", "setScheduling", "setMetadata", "start"], "the claim (labels, fingerprinted) comes first: setScheduling has no fingerprint, so a racer that lost the claim must not have written it");

        using var scheduling = JsonDocument.Parse(rig.Handler.To(Post, OldInstance + "/setScheduling").Single().Body);
        scheduling.RootElement.GetProperty("maxRunDuration").GetProperty("seconds").GetInt64().ShouldBe(5400);
        scheduling.RootElement.GetProperty("instanceTerminationAction").GetString().ShouldBe("DELETE");

        using var metadata = JsonDocument.Parse(rig.Handler.To(Post, OldInstance + "/setMetadata").Single().Body);
        metadata.RootElement.GetProperty("fingerprint").GetString().ShouldBe("mf1");
        var items = metadata.RootElement.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("value").GetString());
        items["deg-job-id"].ShouldBe("job1", "the old job's id must be replaced, or startup.sh would run the old job");
        items["block-project-ssh-keys"].ShouldBe("true");

        using var labels = JsonDocument.Parse(rig.Handler.To(Post, OldInstance + "/setLabels").Single().Body);
        labels.RootElement.GetProperty("labelFingerprint").GetString().ShouldBe("lf1");
        var sent = labels.RootElement.GetProperty("labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        sent["job-id"].ShouldBe("job1");
        sent["app"].ShouldBe("dna-entropy-graph");
        sent.Count.ShouldBe(6);
    }

    // ---- #970: restoring a borrowed VM's labels after a failed or cancelled reuse ----

    private static readonly IReadOnlyDictionary<string, string> DonorLabels = new Dictionary<string, string>
    {
        ["app"] = "dna-entropy-graph",
        ["installation-id"] = "install-1",
        ["job-id"] = "job-old",
        ["lifecycle"] = "keepalive",
    };

    /// <summary>The donor as a reuse for <c>job1</c> left it: stopped, carrying job1's id.</summary>
    private static string ClaimedByJob1() => StoppedOld().Replace("\"job-id\":\"job-old\"", "\"job-id\":\"job1\"");

    private static string DiskClaimedByJob1() => OldDiskBody().Replace("\"job-id\":\"job-old\"", "\"job-id\":\"job1\"");

    [Fact]
    public async Task Restore_writes_the_donors_labels_to_the_instance_then_the_boot_disk_each_under_the_fingerprint_it_just_read()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskClaimedByJob1())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"));

        var state = await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None);

        state.ShouldBe(LabelRestoreState.Restored);
        rig.Handler.Requests.Where(r => r.Method == Post).Select(r => r.Uri.AbsolutePath[r.Uri.AbsolutePath.IndexOf("/zones/", StringComparison.Ordinal)..])
            .ShouldBe([$"/zones/{Zone}/instances/deg-job-old/setLabels", $"/zones/{Zone}/disks/deg-job-old/setLabels"], "round 3 finding 4: the instance first, because every job-id lookup reads the instance's labels; a failed disk write then costs nothing a lookup sees");
        foreach (var (path, fingerprint) in new[] { (OldDisk + "/setLabels", "df1"), (OldInstance + "/setLabels", "lf1") })
        {
            using var body = JsonDocument.Parse(rig.Handler.To(Post, path).Single().Body);
            body.RootElement.GetProperty("labelFingerprint").GetString().ShouldBe(fingerprint);
            var sent = body.RootElement.GetProperty("labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            sent.ShouldBe(DonorLabels.ToDictionary(p => p.Key, p => (string?)p.Value));
        }
    }

    [Fact]
    public async Task D970r3_a_boot_disk_write_that_fails_after_the_instance_was_restored_is_still_restored()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskClaimedByJob1())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldDisk + "/setLabels", 403, RpcError(403, "PERMISSION_DENIED", "no"));

        var state = await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None);

        state.ShouldBe(LabelRestoreState.Restored, "the instance's labels are what every job-id lookup reads, and they are back: a disk that kept the dead job's label costs nothing a lookup sees");
        rig.Handler.To(Post, OldInstance + "/setLabels").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Restore_of_a_VM_that_no_longer_carries_the_borrowing_jobs_id_writes_nothing()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 200, StoppedOld().Replace("\"job-id\":\"job-old\"", "\"job-id\":\"job-newer\""));

        var state = await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None);

        state.ShouldBe(LabelRestoreState.NewerOwner);
        rig.Handler.Requests.ShouldAllBe(r => r.Method == Get, "a newer run's relabel is never clobbered");
    }

    [Fact]
    public async Task D970r4_an_instance_owned_by_the_borrowing_job_is_restored_even_when_the_boot_disk_carries_a_third_jobs_id_and_the_disk_is_left_alone()
    {
        // Round 4 finding 1 (replaces the round 3 test that returned NewerOwner on a foreign disk): the INSTANCE is the ownership authority (a claim writes it first, and every job-id lookup reads it).
        // Disk writes are best effort, so a disk can carry a dead job's id; that must not leave the instance under the borrowing job's labels.
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskWith("job-dead"))
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"));

        var state = await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None);

        state.ShouldBe(LabelRestoreState.Restored);
        rig.Handler.To(Post, OldInstance + "/setLabels").Count.ShouldBe(1, "the instance is written");
        rig.Handler.To(Post, OldDisk + "/setLabels").ShouldBeEmpty("a disk under another job is never overwritten");
    }

    [Fact]
    public async Task D970r4_a_boot_disk_write_that_throws_something_other_than_a_cloud_error_is_still_restored()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskClaimedByJob1())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Calls(Post, OldDisk + "/setLabels", _ => throw new InvalidOperationException("boom"));

        var state = await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None);

        state.ShouldBe(LabelRestoreState.Restored);
        rig.Handler.To(Post, OldDisk + "/setLabels").Count.ShouldBe(1, "the disk write was attempted, and its failure swallowed");
    }

    [Fact]
    public async Task D970r5_a_412_whose_re_read_finds_the_VM_deleted_is_gone_not_an_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "fingerprint"))
            .Returns(Get, OldInstance, 404, RpcError(404, "NOT_FOUND", "gone"));

        (await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Gone);
    }

    [Fact]
    public async Task D970r3_a_boot_disk_already_under_the_donors_own_job_still_lets_the_instance_be_restored()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, OldDiskBody()) // the disk never took the new job's labels, or got the donor's back
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"));

        (await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Restored);

        rig.Handler.To(Post, OldDisk + "/setLabels").ShouldBeEmpty("nothing to write to a disk that is already the donor's");
        rig.Handler.To(Post, OldInstance + "/setLabels").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_precondition_failure_after_a_newer_run_moved_the_labels_is_a_newer_owner_not_an_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskClaimedByJob1())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "Labels fingerprint either invalid or resource labels have changed"))
            .Returns(Get, OldInstance, 200, StoppedOld().Replace("\"job-id\":\"job-old\"", "\"job-id\":\"job-newer\"").Replace("lf1", "lf9"));

        var state = await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None);

        state.ShouldBe(LabelRestoreState.NewerOwner);
    }

    [Fact]
    public async Task A_precondition_failure_when_the_labels_already_equal_the_donors_is_a_restored_VM_whose_answer_was_lost()
    {
        var rig = new GoogleGatewayHarness();
        var restored = StoppedOld().Replace("\"labels\":{\"app\":\"dna-entropy-graph\",\"job-id\":\"job-old\",\"installation-id\":\"install-1\"}", "\"labels\":{\"app\":\"dna-entropy-graph\",\"job-id\":\"job-old\",\"installation-id\":\"install-1\",\"lifecycle\":\"keepalive\"}");
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskClaimedByJob1())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "Labels fingerprint either invalid or resource labels have changed"))
            .Returns(Get, OldInstance, 200, restored);

        (await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Restored);
    }

    [Fact]
    public async Task A_precondition_failure_with_the_borrowing_jobs_labels_still_on_the_VM_is_an_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskClaimedByJob1())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "fingerprint"))
            .Returns(Get, OldInstance, 200, ClaimedByJob1());

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None));

        thrown.Error.HttpStatus.ShouldBe(412);
    }

    private static string RestoredOld() => StoppedOld().Replace("\"labels\":{\"app\":\"dna-entropy-graph\",\"job-id\":\"job-old\",\"installation-id\":\"install-1\"}", "\"labels\":{\"app\":\"dna-entropy-graph\",\"job-id\":\"job-old\",\"installation-id\":\"install-1\",\"lifecycle\":\"keepalive\"}");

    private static string DiskWith(string jobId) => OldDiskBody().Replace("\"job-id\":\"job-old\"", "\"job-id\":\"" + jobId + "\"");

    [Fact]
    public async Task D970r3_a_disk_write_refused_with_412_after_the_instance_was_restored_is_still_restored()
    {
        // The instance is written first now: whatever the disk's write says (the donor's labels already there, another owner, or still the failed job's), the instance every lookup reads is back.
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, ClaimedByJob1())
            .Returns(Get, OldDisk, 200, DiskClaimedByJob1())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldDisk + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "fingerprint"));

        (await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Restored);

        rig.Handler.To(Post, OldInstance + "/setLabels").Count.ShouldBe(1);
    }

    [Fact]
    public async Task D970r2_a_replay_after_a_lost_answer_finds_the_donors_labels_already_on_the_VM_and_is_restored_not_a_newer_owner()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 200, RestoredOld());

        (await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Restored);

        rig.Handler.Requests.ShouldAllBe(r => r.Method == Get, "nothing is written: the labels are already the donor's");
    }

    [Fact]
    public async Task Restore_of_a_VM_that_is_gone_is_gone_not_an_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 404, RpcError(404, "NOT_FOUND", "gone"));

        (await rig.Gateways.Compute.RestoreVmLabelsAsync("deg-job-old", Zone, "job1", DonorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Gone);
    }

    private static string Labelled(string body, VmSpec spec)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        node["labelFingerprint"] = "lf2";
        node["labels"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(spec.ToLabels()));
        return node.ToJsonString();
    }

    private static string WithDiskSource(string source)
        => StoppedOld().Replace(DiskSource, source);

    // Issue #678 review: an instance with no boot disk cannot be relabelled; skipping would leave the old job's labels on a disk.
    [Fact]
    public async Task A_reuse_that_sees_no_boot_disk_throws_before_any_write()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 200, StoppedOld().Replace("\"disks\":[{\"boot\":true,\"source\":\"" + DiskSource + "\"}],", string.Empty));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.Code.ShouldBe("VM_NO_BOOT_DISK");
        rig.Handler.Requests.ShouldAllBe(r => r.Method == Get, "nothing is written when the boot disk cannot be found");
    }

    [Fact]
    public async Task A_precondition_failure_on_the_instance_labels_is_accepted_when_the_labels_already_equal_the_target()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "fingerprint", "conditionNotMet"))
            .Returns(Get, OldInstance, 200, Labelled(StoppedOld(), Spec()))
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setScheduling", 200, Op("DONE", "setScheduling"))
            .Returns(Post, OldInstance + "/setMetadata", 200, Op("DONE", "setMetadata"))
            .Returns(Post, OldInstance + "/start", 200, Op("DONE", "start"))
            .Returns(Get, OldInstance, 200, Vm("deg-job-old", Zone, "RUNNING", job: "job1"));

        var vm = await rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None);

        vm.Name.ShouldBe("deg-job-old");
        rig.Handler.To(Post, OldInstance + "/start").Count.ShouldBe(1, "a lost answer to our own label write must not kill the reuse");
    }

    [Fact]
    public async Task A_precondition_failure_on_the_instance_labels_stays_a_lost_claim_when_another_job_holds_them()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "fingerprint", "conditionNotMet"))
            .Returns(Get, OldInstance, 200, StoppedOld().Replace("job-old", "job-rival"));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.HttpStatus.ShouldBe(412);
        rig.Handler.Requests.Where(r => r.Method == Post).Count().ShouldBe(1, "the rival's VM is not edited further");
    }

    [Fact]
    public async Task A_precondition_failure_on_the_disk_labels_is_accepted_when_the_disk_already_carries_the_target()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "fingerprint", "conditionNotMet"))
            .Returns(Get, OldDisk, 200, Labelled(OldDiskBody(), Spec()))
            .Returns(Post, OldInstance + "/setScheduling", 200, Op("DONE", "setScheduling"))
            .Returns(Post, OldInstance + "/setMetadata", 200, Op("DONE", "setMetadata"))
            .Returns(Post, OldInstance + "/start", 200, Op("DONE", "start"))
            .Returns(Get, OldInstance, 200, Vm("deg-job-old", Zone, "RUNNING", job: "job1"));

        await rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None);

        rig.Handler.To(Post, OldInstance + "/start").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_precondition_failure_on_the_disk_labels_with_other_labels_present_still_fails()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "fingerprint", "conditionNotMet"))
            .Returns(Get, OldDisk, 200, OldDiskBody());

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.HttpStatus.ShouldBe(412);
        rig.Handler.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_regional_boot_disk_is_relabelled_through_the_regional_disk_API()
    {
        const string regionalDisk = Base + "/regions/us-central1/disks/deg-job-old";
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, WithDiskSource("https://www.googleapis.com/compute/v1/projects/my-lab/regions/us-central1/disks/deg-job-old"))
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Get, regionalDisk, 200, OldDiskBody())
            .Returns(Post, regionalDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setScheduling", 200, Op("DONE", "setScheduling"))
            .Returns(Post, OldInstance + "/setMetadata", 200, Op("DONE", "setMetadata"))
            .Returns(Post, OldInstance + "/start", 200, Op("DONE", "start"))
            .Returns(Get, OldInstance, 200, Vm("deg-job-old", Zone, "RUNNING", job: "job1"));

        await rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None);

        rig.Handler.To(Post, regionalDisk + "/setLabels").Count.ShouldBe(1, "a regional disk has no zonal URL; the zonal call would 404");
    }

    [Theory]
    [InlineData("projects/my-lab/zones/us-central1-a/disks/deg-job-old")]
    [InlineData("https://compute.googleapis.com/compute/beta/projects/my-lab/zones/us-central1-a/disks/deg-job-old/")]
    [InlineData("https://www.googleapis.com/compute/v1/projects/my-lab/zones/us-central1-a/disks/deg-job-old?x=1")]
    public async Task An_odd_but_valid_boot_disk_source_still_reaches_the_zonal_disk(string source)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, WithDiskSource(source))
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setScheduling", 200, Op("DONE", "setScheduling"))
            .Returns(Post, OldInstance + "/setMetadata", 200, Op("DONE", "setMetadata"))
            .Returns(Post, OldInstance + "/start", 200, Op("DONE", "start"))
            .Returns(Get, OldInstance, 200, Vm("deg-job-old", Zone, "RUNNING", job: "job1"));

        await rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None);

        rig.Handler.To(Post, OldDisk + "/setLabels").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_boot_disk_source_that_names_no_disk_throws_rather_than_skipping()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 200, WithDiskSource("https://www.googleapis.com/compute/v1/projects/my-lab/zones/us-central1-a/"));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.Code.ShouldBe("VM_NO_BOOT_DISK");
    }

    [Fact]
    public async Task A_lost_claim_changes_nothing_else_about_the_VM()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Post, OldInstance + "/setLabels", 412, RpcError(412, "FAILED_PRECONDITION", "Labels fingerprint either invalid or resource labels have changed", "conditionNotMet"))
            .Returns(Get, OldInstance, 200, StoppedOld());

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.HttpStatus.ShouldBe(412);
        thrown.Kind.ShouldBe(CloudErrorKind.Other, "a lost claim is not an org-policy refusal (that would abort the run) nor a stockout");
        rig.Handler.Requests.Where(r => r.Method == Post).Select(r => r.Uri.AbsolutePath[(r.Uri.AbsolutePath.LastIndexOf('/') + 1)..])
            .ShouldBe(["setLabels"], "the winner's scheduling and metadata must not be overwritten by a racer that lost the claim");
    }

    [Fact]
    public async Task A_reused_VM_reports_its_last_start_apart_from_its_creation()
    {
        var rig = new GoogleGatewayHarness();
        var started = StoppedOld("RUNNING").Replace("\"creationTimestamp\":\"2026-10-03T01:02:03.000-07:00\",", "\"creationTimestamp\":\"2026-10-03T01:02:03.000-07:00\",\"lastStartTimestamp\":\"2026-10-09T05:06:07.000-07:00\",");
        rig.Handler.Returns(Get, OldInstance, 200, started);

        var vm = (await rig.Gateways.Compute.GetVmAsync("deg-job-old", Zone, CancellationToken.None))!;

        vm.CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 3, 1, 2, 3, TimeSpan.FromHours(-7)));
        vm.StartedAt.ShouldBe(new DateTimeOffset(2026, 10, 9, 5, 6, 7, TimeSpan.FromHours(-7)));
        vm.RunClockStart.ShouldBe(vm.StartedAt);
    }

    // Issue #691: the platform's own clock rides on every answer (the HTTP Date header), so the app can judge a VM's age without trusting the laptop's clock.
    [Fact]
    public async Task A_VM_read_carries_the_servers_own_time_from_the_Date_header()
    {
        var rig = new GoogleGatewayHarness();
        var serverTime = new DateTimeOffset(2026, 10, 9, 12, 30, 15, TimeSpan.Zero);
        rig.Handler.Calls(Get, OldInstance, _ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(StoppedOld("RUNNING"), System.Text.Encoding.UTF8, "application/json") };
            response.Headers.Date = serverTime;
            return Task.FromResult(response);
        });

        var vm = (await rig.Gateways.Compute.GetVmAsync("deg-job-old", Zone, CancellationToken.None))!;

        vm.ServerNow.ShouldBe(serverTime);
    }

    // Issue #691 review: a cached or proxied answer replays an OLD Date; its Age header says how old. It is not the platform's time now: ignored.
    [Theory]
    [InlineData(3600)]
    [InlineData(5)]
    public async Task A_replayed_answer_with_an_Age_header_carries_no_server_time(int ageSeconds)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Calls(Get, OldInstance, _ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(StoppedOld("RUNNING"), System.Text.Encoding.UTF8, "application/json") };
            response.Headers.Date = new DateTimeOffset(2026, 10, 9, 12, 30, 15, TimeSpan.Zero);
            response.Headers.Age = TimeSpan.FromSeconds(ageSeconds);
            return Task.FromResult(response);
        });

        var vm = (await rig.Gateways.Compute.GetVmAsync("deg-job-old", Zone, CancellationToken.None))!;

        vm.ServerNow.ShouldBeNull();
        vm.Name.ShouldBe("deg-job-old", "the VM itself is still read");
    }

    // Issue #691 review: GetVmAsync reads the raw answer (for its Date header) instead of ExecuteAsync. Whatever Google answers with, it must raise the
    // exception ExecuteAsync raises (same code, status, message and kind), or the classification of every VM look would quietly differ. The reuse path's own
    // instances.get still goes through ExecuteAsync, so it is the reference.
    private static void ScriptEveryAttempt(GoogleGatewayHarness rig, int status, string body, string contentType = "application/json")
    {
        for (var i = 0; i < 6; i++)
        {
            rig.Handler.Calls(Get, OldInstance, _ => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) }));
        }
    }

    private static async Task<CloudOperationException> ThrownByGetAsync(int status, string body, string contentType = "application/json")
    {
        var rig = new GoogleGatewayHarness();
        ScriptEveryAttempt(rig, status, body, contentType);
        return await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.GetVmAsync("deg-job-old", Zone, CancellationToken.None));
    }

    private static async Task<CloudOperationException> ThrownByExecuteAsync(int status, string body, string contentType = "application/json")
    {
        var rig = new GoogleGatewayHarness();
        ScriptEveryAttempt(rig, status, body, contentType);
        return await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));
    }

    private const string QuotaFailureBody = "{\"error\":{\"code\":403,\"message\":\"Request denied.\",\"status\":\"PERMISSION_DENIED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.QuotaFailure\",\"violations\":[{\"subject\":\"project:my-lab\",\"description\":\"GPUS_ALL_REGIONS\"}]}]}}";

    [Fact]
    public async Task A_403_SERVICE_DISABLED_on_a_VM_look_is_ApiDisabled_exactly_as_ExecuteAsync_reports_it()
    {
        var body = RpcError(403, "PERMISSION_DENIED", "Request failed.", "SERVICE_DISABLED");

        var raw = await ThrownByGetAsync(403, body);
        var reference = await ThrownByExecuteAsync(403, body);

        raw.Kind.ShouldBe(CloudErrorKind.ApiDisabled);
        raw.Error.ShouldBe(reference.Error);
        raw.Kind.ShouldBe(reference.Kind);
    }

    [Fact]
    public async Task A_403_with_a_QuotaFailure_detail_on_a_VM_look_is_Quota_exactly_as_ExecuteAsync_reports_it()
    {
        var raw = await ThrownByGetAsync(403, QuotaFailureBody);
        var reference = await ThrownByExecuteAsync(403, QuotaFailureBody);

        raw.Kind.ShouldBe(CloudErrorKind.Quota);
        raw.Error.ShouldBe(reference.Error);
        raw.Kind.ShouldBe(reference.Kind);
    }

    [Theory]
    [InlineData(502, "<html><body><h1>502 Bad Gateway</h1></body></html>", "text/html")]
    [InlineData(500, "upstream connect error", "text/plain")]
    [InlineData(403, "<html>Forbidden</html>", "text/html")]
    [InlineData(400, "", "application/json")]
    public async Task A_non_JSON_error_body_on_a_VM_look_gives_the_same_exception_as_ExecuteAsync(int status, string body, string contentType)
    {
        var raw = await ThrownByGetAsync(status, body, contentType);
        var reference = await ThrownByExecuteAsync(status, body, contentType);

        raw.Error.ShouldBe(reference.Error);
        raw.Kind.ShouldBe(reference.Kind);
        raw.Error.HttpStatus.ShouldBe(status);
    }

    [Fact]
    public async Task A_401_on_a_VM_look_refreshes_the_token_and_replays_with_the_new_one()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 401, RpcError(401, "UNAUTHENTICATED", "Request had invalid authentication credentials."))
            .Returns(Get, OldInstance, 200, StoppedOld("RUNNING"));

        var vm = (await rig.Gateways.Compute.GetVmAsync("deg-job-old", Zone, CancellationToken.None))!;

        vm.Name.ShouldBe("deg-job-old");
        rig.Tokens.Refreshes.ShouldBe(1);
        rig.Handler.To(Get, OldInstance).Select(c => c.Authorization).ShouldBe(["Bearer ya29.test-token-1", "Bearer ya29.test-token-2"]);
    }

    [Fact]
    public async Task A_VM_read_without_a_Date_header_has_no_server_time_and_is_still_read()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 200, StoppedOld("RUNNING"));

        var vm = (await rig.Gateways.Compute.GetVmAsync("deg-job-old", Zone, CancellationToken.None))!;

        vm.ServerNow.ShouldBeNull();
        vm.Name.ShouldBe("deg-job-old");
    }

    [Fact]
    public async Task Reuse_of_a_VM_that_is_not_stopped_is_refused_before_any_change()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, OldInstance, 200, StoppedOld("RUNNING"));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.Code.ShouldBe("VM_NOT_STOPPED");
        rig.Handler.Requests.ShouldAllBe(r => r.Method == Get, "a VM that is not stopped must not be edited");
    }

    [Fact]
    public async Task A_start_that_runs_out_of_capacity_is_a_stockout_after_the_VM_was_claimed()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, OldInstance, 200, StoppedOld())
            .Returns(Get, OldDisk, 200, OldDiskBody())
            .Returns(Post, OldDisk + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/setScheduling", 200, Op("DONE", "setScheduling"))
            .Returns(Post, OldInstance + "/setMetadata", 200, Op("DONE", "setMetadata"))
            .Returns(Post, OldInstance + "/setLabels", 200, Op("DONE", "setLabels"))
            .Returns(Post, OldInstance + "/start", 200, Op("DONE", "start", OpError("ZONE_RESOURCE_POOL_EXHAUSTED", "The zone does not have enough resources available to fulfill the request."), 503));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.ReuseVmAsync(Spec(), "deg-job-old", Zone, CancellationToken.None));

        thrown.Kind.ShouldBe(CloudErrorKind.Stockout);
    }

    [Fact]
    public async Task Calls_that_need_a_project_fail_loudly_when_none_is_selected()
    {
        var rig = new GoogleGatewayHarness();
        var none = new CloudScope(string.Empty);

        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Compute.GetVmAsync(none, "deg-job1", Zone, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Gateways.Compute.ListByInstallationAsync(none, "install-1", CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    // ---- Issue #92: the wire body and the shared bound, through the real gateway ----

    private static VmSpec FlexSpec(TimeSpan wait, TimeSpan margin) => Spec() with { FlexStart = true, FlexStartWait = wait, FlexStartMargin = margin };

    [Fact]
    public async Task A_flex_start_create_sends_the_provisioning_model_and_the_wait_for_capacity_in_the_insert_body()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, Instances, 200, Op("PENDING"))
            .Returns(Get, OperationPath, 200, Op("DONE"))
            .Returns(Get, Instance, 200, Vm("deg-job1", Zone, "PROVISIONING"));

        await rig.Gateways.Compute.CreateVmAsync(FlexSpec(TimeSpan.FromHours(2), TimeSpan.FromMinutes(5)), Zone, CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Post, Instances).Single().Body);
        body.RootElement.GetProperty("scheduling").GetProperty("provisioningModel").GetString().ShouldBe("FLEX_START");
        body.RootElement.GetProperty("scheduling").GetProperty("instanceTerminationAction").GetString().ShouldBe("DELETE");
        body.RootElement.GetProperty("params").GetProperty("requestValidForDuration").GetProperty("seconds").GetInt64().ShouldBe(7200);
    }

    [Fact]
    public async Task A_queued_create_is_polled_for_the_specs_own_bound_not_the_ordinary_operation_deadline()
    {
        // The ordinary deadline is two minutes (the harness default); the spec's bound is 4 s. Eight RUNNING answers are scripted: only the bound can end the poll before they run out.
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Post, Instances, 200, Op("PENDING"));
        for (var i = 0; i < 8; i++)
        {
            rig.Handler.Returns(Get, OperationPath, 200, Op("RUNNING"));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Compute.CreateVmAsync(FlexSpec(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)), Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe(OperationPoller.TimeoutCode);
        rig.Delays.Count.ShouldBeLessThan(8, "the poll ended at the 4 s bound, before the scripted answers ran out");
    }
}
