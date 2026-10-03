using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #52: the real Service Usage v1 gateway over a scripted HTTP handler. Payloads are Google's documented REST
/// shapes (services:batchEnable returning an operation, operations.get, services.get, google.rpc.Status errors), not
/// captured from a live project: there is no GCP access in this build. The decisive observable is the request itself.
/// </summary>
public class GoogleServiceUsageGatewayTests
{
    private const string Compute = "/v1/projects/my-lab/services/compute.googleapis.com";
    private const string Storage = "/v1/projects/my-lab/services/storage.googleapis.com";
    private const string Quotas = "/v1/projects/my-lab/services/cloudquotas.googleapis.com";
    private const string BatchEnable = "/v1/projects/my-lab/services:batchEnable";
    private const string OperationPath = "/v1/operations/acc.4242";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;

    private const string OperationPending = """{"name":"operations/acc.4242","metadata":{"@type":"type.googleapis.com/google.api.serviceusage.v1.OperationMetadata"},"done":false}""";

    private const string OperationDone = """{"name":"operations/acc.4242","done":true,"response":{"@type":"type.googleapis.com/google.api.serviceusage.v1.BatchEnableServicesResponse"}}""";

    private static string Service(string id, string state)
        => "{\"name\":\"projects/123/services/" + id + "\",\"state\":\"" + state + "\",\"parent\":\"projects/123\"}";

    private static string RpcError(int http, string status, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"}}";

    private static void AllEnabled(ScriptedHttpHandler handler)
    {
        handler
            .Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLED"))
            .Returns(Get, Storage, 200, Service("storage.googleapis.com", "ENABLED"))
            .Returns(Get, Quotas, 200, Service("cloudquotas.googleapis.com", "ENABLED"));
    }

    [Fact]
    public async Task Enabling_posts_the_three_service_ids_polls_every_five_seconds_then_confirms_each_is_ENABLED()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, BatchEnable, 200, OperationPending)
            .Returns(Get, OperationPath, 200, OperationPending)
            .Returns(Get, OperationPath, 200, OperationDone);
        AllEnabled(rig.Handler);

        await rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None);

        var enable = rig.Handler.To(Post, BatchEnable).Single();
        enable.Uri.Host.ShouldBe("serviceusage.googleapis.com");
        enable.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        using var body = JsonDocument.Parse(enable.Body);
        body.RootElement.GetProperty("serviceIds").EnumerateArray().Select(e => e.GetString()).ShouldBe(["compute.googleapis.com", "storage.googleapis.com", "cloudquotas.googleapis.com"]);

        rig.Handler.To(Get, OperationPath).Count.ShouldBe(2);
        rig.Delays.ShouldBe([TimeSpan.FromSeconds(5)]);
        foreach (var path in new[] { Compute, Storage, Quotas })
        {
            rig.Handler.To(Get, path).Single().Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        }
    }

    [Fact]
    public async Task A_finished_operation_with_a_service_still_ENABLING_keeps_asking_services_get_every_five_seconds()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, BatchEnable, 200, OperationDone);
        rig.Handler
            .Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLING"))
            .Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLED"))
            .Returns(Get, Storage, 200, Service("storage.googleapis.com", "ENABLED"))
            .Returns(Get, Quotas, 200, Service("cloudquotas.googleapis.com", "ENABLED"));

        await rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None);

        rig.Handler.To(Get, Compute).Count.ShouldBe(2);
        rig.Delays.ShouldBe([TimeSpan.FromSeconds(5)]);
        rig.Handler.To(Get, OperationPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_service_that_never_reaches_ENABLED_after_the_operation_finished_times_out_instead_of_reporting_success()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromSeconds(10), retries: 0);
        rig.Handler.Returns(Post, BatchEnable, 200, OperationDone);
        for (var i = 0; i < 10; i++)
        {
            rig.Handler.Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLING"));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        rig.Handler.To(Get, Storage).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_operation_that_never_finishes_times_out_as_a_network_error_at_the_deadline()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromSeconds(10), retries: 0);
        rig.Handler.Returns(Post, BatchEnable, 200, OperationPending);
        for (var i = 0; i < 10; i++)
        {
            rig.Handler.Returns(Get, OperationPath, 200, OperationPending);
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Delays.Sum(d => d.TotalSeconds).ShouldBe(10);
    }

    [Fact]
    public async Task A_403_on_the_enable_call_is_NOT_PROJECT_OWNER_and_is_not_retried()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, BatchEnable, 403, RpcError(403, "PERMISSION_DENIED", "Permission denied to enable service [compute.googleapis.com]"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.NotProjectOwner);
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.To(Post, BatchEnable).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_permission_error_reported_by_the_polled_operation_is_NOT_PROJECT_OWNER()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, BatchEnable, 200, OperationPending)
            .Returns(Get, OperationPath, 200, """{"name":"operations/acc.4242","done":true,"error":{"code":7,"message":"Permission denied to enable service [storage.googleapis.com]"}}""");

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.NotProjectOwner);
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task Enabling_on_a_project_without_billing_keeps_its_billing_kind_not_the_owner_code()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, BatchEnable, 400, RpcError(400, "FAILED_PRECONDITION", "Billing account for project 'projects/123' is not found. Billing must be enabled for activation of service(s) 'compute.googleapis.com'."));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Billing);
        ex.Error.Code.ShouldBe("FAILED_PRECONDITION");
    }

    private static string RpcErrorWithReason(int http, string status, string message, string reason)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status
            + "\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"" + reason + "\",\"domain\":\"googleapis.com\"}]}}";

    [Fact]
    public async Task A_403_because_Service_Usage_itself_is_off_is_api_disabled_not_NOT_PROJECT_OWNER()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, BatchEnable, 403, RpcErrorWithReason(403, "PERMISSION_DENIED", "Service Usage API has not been used in project 123 before or it is disabled.", "SERVICE_DISABLED"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.ApiDisabled);
        ex.Error.Code.ShouldNotBe(SetupErrorCodes.NotProjectOwner);
    }

    [Fact]
    public async Task A_403_that_says_billing_is_off_is_billing_not_NOT_PROJECT_OWNER()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, BatchEnable, 403, RpcErrorWithReason(403, "PERMISSION_DENIED", "This API method requires billing to be enabled.", "BILLING_DISABLED"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Billing);
        ex.Error.Code.ShouldNotBe(SetupErrorCodes.NotProjectOwner);
    }

    [Fact]
    public async Task A_403_from_an_organization_policy_is_org_policy_not_NOT_PROJECT_OWNER()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, BatchEnable, 403, RpcError(403, "PERMISSION_DENIED", "Operation denied by org policy: [constraints/serviceuser.services] violated."));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
        ex.Error.Code.ShouldNotBe(SetupErrorCodes.NotProjectOwner);
    }

    [Fact]
    public async Task A_service_that_never_reaches_ENABLED_posts_batchEnable_once_even_with_the_default_retries()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromSeconds(10), retries: 3);
        rig.Handler.Returns(Post, BatchEnable, 200, OperationDone);
        for (var i = 0; i < 40; i++)
        {
            rig.Handler.Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLING"));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        rig.Handler.To(Post, BatchEnable).Count.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_whole_enable_call_waits_at_most_one_deadline_not_one_per_phase()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromSeconds(20), retries: 0);
        rig.Handler.Returns(Post, BatchEnable, 200, OperationPending);

        // The operation finishes after 10 virtual seconds; the services then never read ENABLED.
        rig.Handler.Returns(Get, OperationPath, 200, OperationPending).Returns(Get, OperationPath, 200, OperationDone);
        for (var i = 0; i < 40; i++)
        {
            rig.Handler.Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLING"));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        rig.Delays.Sum(d => d.TotalSeconds).ShouldBe(20);
    }

    [Fact]
    public async Task A_transient_error_on_a_poll_read_is_retried_on_its_own_and_never_posts_batchEnable_again()
    {
        var rig = new GoogleGatewayHarness(retries: 3);
        rig.Handler.Returns(Post, BatchEnable, 200, OperationDone);
        for (var i = 0; i < 3; i++)
        {
            rig.Handler.Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLING"));
        }

        rig.Handler
            .Returns(Get, Compute, 503, RpcError(503, "UNAVAILABLE", "The service is currently unavailable."))
            .Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLED"))
            .Returns(Get, Storage, 200, Service("storage.googleapis.com", "ENABLED"))
            .Returns(Get, Quotas, 200, Service("cloudquotas.googleapis.com", "ENABLED"));

        await rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None);

        rig.Handler.To(Post, BatchEnable).Count.ShouldBe(1);
        rig.Handler.To(Get, Compute).Count.ShouldBe(5);
        rig.Log.Retries.Count.ShouldBe(1);
        rig.Delays.Sum(d => d.TotalSeconds).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(2).TotalSeconds);
    }

    [Fact]
    public async Task A_transient_error_on_the_operation_poll_is_retried_on_its_own_and_never_posts_batchEnable_again()
    {
        var rig = new GoogleGatewayHarness(retries: 3);
        rig.Handler
            .Returns(Post, BatchEnable, 200, OperationPending)
            .Returns(Get, OperationPath, 503, RpcError(503, "UNAVAILABLE", "The service is currently unavailable."))
            .Returns(Get, OperationPath, 200, OperationDone);
        AllEnabled(rig.Handler);

        await rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None);

        rig.Handler.To(Post, BatchEnable).Count.ShouldBe(1);
        rig.Handler.To(Get, OperationPath).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_hung_services_get_ends_at_the_deadline_as_the_poll_timeout_after_one_post()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromMilliseconds(300), retries: 3);
        rig.Handler.Returns(Post, BatchEnable, 200, OperationDone).Hangs(Get, Compute);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        rig.Handler.To(Post, BatchEnable).Count.ShouldBe(1);
        rig.Handler.To(Get, Compute).Count.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_callers_cancel_mid_poll_is_a_cancel_and_is_not_replayed()
    {
        var rig = new GoogleGatewayHarness(retries: 3);
        using var cts = new CancellationTokenSource();
        rig.Handler
            .Returns(Post, BatchEnable, 200, OperationDone)
            .Calls(Get, Compute, async token =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => rig.Gateways.Services.EnableServicesAsync("my-lab", RequiredServices.Ids, cts.Token));

        rig.Handler.To(Post, BatchEnable).Count.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Enabling_through_preflight_never_replays_the_post_when_a_poll_read_keeps_failing()
    {
        var rig = new GoogleGatewayHarness(retries: 2);
        rig.Handler.Returns(Post, BatchEnable, 200, OperationDone);
        for (var i = 0; i < 12; i++)
        {
            rig.Handler.Returns(Get, Compute, 503, RpcError(503, "UNAVAILABLE", "The service is currently unavailable."));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.ProjectSetup.EnableComputeApiAsync("my-lab", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.To(Post, BatchEnable).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("ENABLED", true)]
    [InlineData("DISABLED", false)]
    [InlineData("STATE_UNSPECIFIED", false)]
    public async Task A_service_is_enabled_only_when_its_state_is_ENABLED(string state, bool expected)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, Compute, 200, Service("compute.googleapis.com", state));

        (await rig.Gateways.Services.IsServiceEnabledAsync("my-lab", "compute.googleapis.com", CancellationToken.None)).ShouldBe(expected);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_and_replays_the_service_check()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, Compute, 401, RpcError(401, "UNAUTHENTICATED", "Request had invalid authentication credentials."))
            .Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLED"));

        (await rig.Gateways.Services.IsServiceEnabledAsync("my-lab", "compute.googleapis.com", CancellationToken.None)).ShouldBeTrue();

        rig.Handler.To(Get, Compute).Select(c => c.Authorization).ShouldBe(["Bearer ya29.test-token-1", "Bearer ya29.test-token-2"]);
    }

    // ------------------------------------------------------------------ the composite the preflight uses

    [Fact]
    public async Task The_preflight_gateway_reads_each_step_from_its_own_Google_call()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, "/v3/projects/my-lab", 200, """{"name":"projects/123","projectId":"my-lab","state":"ACTIVE","displayName":"My Lab"}""")
            .Returns(Get, "/v3/projects/gone", 404, RpcError(404, "NOT_FOUND", "Project not found"))
            .Returns(Get, "/v1/projects/my-lab/billingInfo", 200, """{"projectId":"my-lab","billingAccountName":"billingAccounts/AAA","billingEnabled":true}""")
            .Returns(Get, Compute, 200, Service("compute.googleapis.com", "DISABLED"));

        var setup = rig.Gateways.ProjectSetup;

        (await setup.GetProjectStateAsync("my-lab", CancellationToken.None)).ShouldBe(ProjectLifecycleState.Active);
        (await setup.GetProjectStateAsync("gone", CancellationToken.None)).ShouldBe(ProjectLifecycleState.NotFound);
        (await setup.IsBillingEnabledAsync("my-lab", CancellationToken.None)).ShouldBeTrue();
        (await setup.IsComputeApiEnabledAsync("my-lab", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Enabling_the_compute_api_for_preflight_posts_only_compute()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, BatchEnable, 200, OperationDone).Returns(Get, Compute, 200, Service("compute.googleapis.com", "ENABLED"));

        await rig.Gateways.ProjectSetup.EnableComputeApiAsync("my-lab", CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Post, BatchEnable).Single().Body);
        body.RootElement.GetProperty("serviceIds").EnumerateArray().Select(e => e.GetString()).ShouldBe(["compute.googleapis.com"]);
    }
}
