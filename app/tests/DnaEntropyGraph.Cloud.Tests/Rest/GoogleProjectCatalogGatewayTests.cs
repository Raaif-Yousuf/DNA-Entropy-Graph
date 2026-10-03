using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #50: the real Resource Manager v3 gateway, over a scripted HTTP handler. Every payload below is built from
/// Google's documented REST shapes (projects:search, projects.create, operations.get, google.rpc.Status with ErrorInfo
/// and QuotaFailure details), not captured from a live project: there is no GCP access in this build.
/// The decisive observable is the request itself (method, URL, body, bearer token), because a gateway that parses a
/// perfect response but sends the wrong request is the wired-to-nothing bug.
/// </summary>
public class GoogleProjectCatalogGatewayTests
{
    private const string Root = "https://cloudresourcemanager.googleapis.com";
    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;

    private const string SearchPage1 = """
        {"projects":[
          {"name":"projects/101","parent":"organizations/9","projectId":"zeta-lab","state":"ACTIVE","displayName":"Zeta Lab","createTime":"2026-01-02T03:04:05Z"},
          {"name":"projects/102","parent":"organizations/9","projectId":"dna-entropy-aaaa1111","state":"ACTIVE","displayName":"DNA Entropy Graph","labels":{"app":"dna-entropy-graph","installation-id":"inst-1"}}
        ],"nextPageToken":"page-2"}
        """;

    private const string SearchPage2 = """
        {"projects":[
          {"name":"projects/103","projectId":"alpha-lab","state":"ACTIVE","displayName":"Alpha Lab","labels":{"app":"something-else"}}
        ]}
        """;

    private const string CreateOperationPending = """
        {"name":"operations/cp.7001","metadata":{"@type":"type.googleapis.com/google.cloud.resourcemanager.v3.CreateProjectMetadata"},"done":false}
        """;

    private const string CreateOperationDone = """
        {"name":"operations/cp.7001","metadata":{"@type":"type.googleapis.com/google.cloud.resourcemanager.v3.CreateProjectMetadata"},"done":true,
         "response":{"@type":"type.googleapis.com/google.cloud.resourcemanager.v3.Project","name":"projects/555","projectId":"dna-entropy-abcd1234","state":"ACTIVE","displayName":"DNA Entropy Graph","labels":{"app":"dna-entropy-graph","installation-id":"inst-1"}}}
        """;

    private static string RpcError(int http, string status, string message, string? detailsJson = null)
    {
        var details = detailsJson is null ? string.Empty : ",\"details\":" + detailsJson;
        return "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"" + details + "}}";
    }

    private static string OperationError(int rpcCode, string message, string? detailsJson = null)
    {
        var details = detailsJson is null ? string.Empty : ",\"details\":" + detailsJson;
        return "{\"name\":\"operations/cp.7001\",\"done\":true,\"error\":{\"code\":" + rpcCode + ",\"message\":" + JsonSerializer.Serialize(message) + details + "}}";
    }

    private const string QuotaDetails = """
        [{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"subject":"project:dna-entropy-abcd1234","description":"Project creation quota exceeded"}]}]
        """;

    // A per-minute rate limit (documented ErrorInfo reason RATE_LIMIT_EXCEEDED): it also carries a QuotaFailure, and it
    // must still be retried, never reported as "you reached your project limit".
    private const string RateLimitDetails = """
        [{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"RATE_LIMIT_EXCEEDED","domain":"googleapis.com","metadata":{"quota_limit":"RequestsPerMinutePerProject"}},
         {"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"subject":"project:123","description":"Requests per minute exceeded"}]}]
        """;

    private const string OurProjectJson = """{"name":"projects/555","projectId":"dna-entropy-abcd1234","state":"ACTIVE","displayName":"DNA Entropy Graph","labels":{"app":"dna-entropy-graph","installation-id":"inst-1"}}""";

    // ------------------------------------------------------------------ list

    [Fact]
    public async Task Listing_sends_an_authorised_search_for_active_projects_and_follows_the_page_token()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v3/projects:search", 200, SearchPage1).Returns(Get, "/v3/projects:search", 200, SearchPage2);

        var projects = await rig.Gateways.ProjectCatalog.ListActiveProjectsAsync(CancellationToken.None);

        var calls = rig.Handler.To(Get, "/v3/projects:search");
        calls.Count.ShouldBe(2);
        calls[0].Uri.Host.ShouldBe("cloudresourcemanager.googleapis.com");
        Uri.UnescapeDataString(calls[0].Uri.Query).ShouldContain("query=state:ACTIVE");
        calls[0].Uri.Query.ShouldNotContain("pageToken");
        calls[1].Uri.Query.ShouldContain("pageToken=page-2");
        calls.ShouldAllBe(c => c.Authorization == "Bearer " + rig.Tokens.CurrentToken);

        projects.Count.ShouldBe(3);
        projects[0].ProjectId.ShouldBe("dna-entropy-aaaa1111");
        projects[0].IsAppProject.ShouldBeTrue();
        projects.Skip(1).ShouldAllBe(p => !p.IsAppProject);
        projects.Select(p => p.ProjectId).Skip(1).ShouldBe(["alpha-lab", "zeta-lab"]);
        projects.ShouldAllBe(p => p.State == ProjectLifecycleState.Active);
        projects.Single(p => p.ProjectId == "alpha-lab").DisplayName.ShouldBe("Alpha Lab");
    }

    [Fact]
    public async Task A_fresh_account_has_no_projects_and_the_list_is_empty()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v3/projects:search", 200, "{}");

        (await rig.Gateways.ProjectCatalog.ListActiveProjectsAsync(CancellationToken.None)).ShouldBeEmpty();
        rig.Handler.To(Get, "/v3/projects:search").Count.ShouldBe(1);
    }

    // ------------------------------------------------------------------ get

    [Fact]
    public async Task Getting_a_project_maps_its_lifecycle_state()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, "/v3/projects/my-lab", 200, """{"name":"projects/1","projectId":"my-lab","state":"ACTIVE","displayName":"My Lab"}""")
            .Returns(Get, "/v3/projects/going-lab", 200, """{"name":"projects/2","projectId":"going-lab","state":"DELETE_REQUESTED","displayName":"Going"}""");

        var active = await rig.Gateways.ProjectCatalog.GetProjectAsync("my-lab", CancellationToken.None);
        var going = await rig.Gateways.ProjectCatalog.GetProjectAsync("going-lab", CancellationToken.None);

        active!.State.ShouldBe(ProjectLifecycleState.Active);
        going!.State.ShouldBe(ProjectLifecycleState.Other);
        rig.Handler.To(Get, "/v3/projects/my-lab").Single().Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
    }

    [Theory]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(403, "PERMISSION_DENIED")]
    public async Task A_project_that_cannot_be_described_is_null(int http, string status)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v3/projects/ghost-lab", http, RpcError(http, status, "The caller does not have permission"));

        (await rig.Gateways.ProjectCatalog.GetProjectAsync("ghost-lab", CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("SERVICE_DISABLED", "Cloud Resource Manager API has not been used in project 123 before or it is disabled.")]
    [InlineData("SERVICE_DISABLED", "The API is turned off for this project.")]
    [InlineData("BILLING_DISABLED", "This API method requires billing to be enabled.")]
    public async Task A_403_that_is_an_api_off_or_billing_error_surfaces_its_kind_instead_of_pick_another_project(string reason, string message)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            Get,
            "/v3/projects/my-lab",
            403,
            RpcError(403, "PERMISSION_DENIED", message, $$"""[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"{{reason}}","domain":"googleapis.com"}]"""));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.ProjectCatalog.GetProjectAsync("my-lab", CancellationToken.None));

        ex.Kind.ShouldBe(reason == "SERVICE_DISABLED" ? CloudErrorKind.ApiDisabled : CloudErrorKind.Billing);
    }

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Creating_posts_the_id_the_name_and_the_labels_then_polls_the_operation_until_done()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 200, CreateOperationPending)
            .Returns(Get, "/v3/operations/cp.7001", 200, CreateOperationPending)
            .Returns(Get, "/v3/operations/cp.7001", 200, CreateOperationDone);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        var create = rig.Handler.To(Post, "/v3/projects").Single();
        create.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        using var body = JsonDocument.Parse(create.Body);
        body.RootElement.GetProperty("projectId").GetString().ShouldBe("dna-entropy-abcd1234");
        body.RootElement.GetProperty("displayName").GetString().ShouldBe("DNA Entropy Graph");
        var labels = body.RootElement.GetProperty("labels");
        labels.GetProperty("app").GetString().ShouldBe("dna-entropy-graph");
        labels.GetProperty("installation-id").GetString().ShouldBe("inst-1");

        rig.Handler.To(Get, "/v3/operations/cp.7001").Count.ShouldBe(2);
        rig.Delays.ShouldBe([TimeSpan.FromSeconds(1)]);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        project.State.ShouldBe(ProjectLifecycleState.Active);
        project.IsAppProject.ShouldBeTrue();
    }

    [Fact]
    public async Task An_operation_that_is_already_done_is_not_polled_twice()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, "/v3/projects", 200, CreateOperationDone);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        rig.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_project_limit_error_on_the_create_call_is_PROJECT_QUOTA_and_is_not_retried()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, "/v3/projects", 429, RpcError(429, "RESOURCE_EXHAUSTED", "Quota exceeded: project creation quota", QuotaDetails));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.ProjectQuota);
        ex.Kind.ShouldBe(CloudErrorKind.Quota);
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_project_limit_error_reported_by_the_polled_operation_is_PROJECT_QUOTA()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 200, CreateOperationPending)
            .Returns(Get, "/v3/operations/cp.7001", 200, OperationError(8, "Project creation quota exceeded", QuotaDetails));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.ProjectQuota);
        ex.Kind.ShouldBe(CloudErrorKind.Quota);
    }

    [Theory]
    [InlineData("Operation denied by org policy: [constraints/gcp.resourceLocations] violated.")]
    [InlineData("The request violates the organization policy constraints/resourcemanager.allowedExportDestinations.")]
    [InlineData("Creating projects outside a folder is blocked by your Organization Policy.")]
    public async Task An_org_policy_refusal_on_the_create_call_is_ORG_POLICY_BLOCK(string message)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, "/v3/projects", 400, RpcError(400, "FAILED_PRECONDITION", message));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.OrgPolicyBlock);
        ex.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
        rig.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_org_policy_refusal_reported_by_the_polled_operation_is_ORG_POLICY_BLOCK()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 200, CreateOperationPending)
            .Returns(
                Get,
                "/v3/operations/cp.7001",
                200,
                OperationError(9, "Precondition failed.", """[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"ORG_POLICY_VIOLATION","domain":"orgpolicy.googleapis.com"}]"""));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.OrgPolicyBlock);
        ex.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
    }

    [Fact]
    public async Task Missing_permission_to_create_projects_is_the_PERMISSION_setup_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, "/v3/projects", 403, RpcError(403, "PERMISSION_DENIED", "Permission 'resourcemanager.projects.create' denied on resource"));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        ex.Error.Code.ShouldBe(SetupErrorCodes.Permission);
        ex.Error.HttpStatus.ShouldBe(403);
        SetupErrorCodes.ActionResourceKey(ex.Error.Code).ShouldNotBe("SetupAction_TryAgain");
    }

    [Theory]
    [InlineData("SERVICE_DISABLED", "Cloud Resource Manager API has not been used in project 123 before or it is disabled.", "API_DISABLED")]
    [InlineData("BILLING_DISABLED", "This API method requires billing to be enabled.", "NO_BILLING")]
    public async Task A_permanent_403_on_create_carries_a_setup_code_whose_action_fits(string reason, string message, string expectedCode)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            Post,
            "/v3/projects",
            403,
            RpcError(403, "PERMISSION_DENIED", message, $$"""[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"{{reason}}","domain":"googleapis.com"}]"""));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(expectedCode);
        SetupErrorCodes.ActionResourceKey(ex.Error.Code).ShouldNotBe("SetupAction_TryAgain");
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
    }

    // The mutating POST is retried alone; every poll read is its own idempotent, retried call.

    [Fact]
    public async Task A_transient_error_while_polling_is_retried_on_its_own_and_never_posts_the_create_again()
    {
        var rig = new GoogleGatewayHarness(retries: 3);
        rig.Handler
            .Returns(Post, "/v3/projects", 200, CreateOperationPending)
            .Returns(Get, "/v3/operations/cp.7001", 503, RpcError(503, "UNAVAILABLE", "The service is currently unavailable."))
            .Returns(Get, "/v3/operations/cp.7001", 200, CreateOperationDone);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
        rig.Handler.To(Get, "/v3/operations/cp.7001").Count.ShouldBe(2);
        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_poll_read_that_never_recovers_ends_as_a_network_error_after_one_create()
    {
        var rig = new GoogleGatewayHarness(retries: 2);
        rig.Handler.Returns(Post, "/v3/projects", 200, CreateOperationPending);
        for (var i = 0; i < 3; i++)
        {
            rig.Handler.Returns(Get, "/v3/operations/cp.7001", 503, RpcError(503, "UNAVAILABLE", "The service is currently unavailable."));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
        rig.Handler.To(Get, "/v3/operations/cp.7001").Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_hung_poll_read_ends_at_the_deadline_as_the_poll_timeout_after_one_create()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromMilliseconds(300), retries: 3);
        rig.Handler
            .Returns(Post, "/v3/projects", 200, CreateOperationPending)
            .Hangs(Get, "/v3/operations/cp.7001");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
        rig.Handler.To(Get, "/v3/operations/cp.7001").Count.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_poll_read_that_times_out_in_the_http_client_is_retried_alone_and_never_posts_the_create_again()
    {
        var rig = new GoogleGatewayHarness(retries: 3);
        rig.Handler
            .Returns(Post, "/v3/projects", 200, CreateOperationPending)
            .Calls(Get, "/v3/operations/cp.7001", _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."))
            .Returns(Get, "/v3/operations/cp.7001", 200, CreateOperationDone);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_callers_own_cancel_mid_poll_surfaces_as_a_cancel_and_is_not_replayed()
    {
        var rig = new GoogleGatewayHarness(retries: 3);
        using var cts = new CancellationTokenSource();
        rig.Handler
            .Returns(Post, "/v3/projects", 200, CreateOperationPending)
            .Calls(Get, "/v3/operations/cp.7001", async token =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });

        await Should.ThrowAsync<OperationCanceledException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", cts.Token));

        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
        rig.Handler.To(Get, "/v3/operations/cp.7001").Count.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_operation_done_with_no_response_falls_back_to_reading_the_requested_project()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 200, """{"name":"operations/cp.7001","done":true}""")
            .Returns(Get, "/v3/projects/dna-entropy-abcd1234", 200, OurProjectJson);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        project.IsAppProject.ShouldBeTrue();
    }

    [Fact]
    public async Task An_operation_done_with_no_response_and_no_project_is_an_error_never_an_empty_success()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 200, """{"name":"operations/cp.7001","done":true}""")
            .Returns(Get, "/v3/projects/dna-entropy-abcd1234", 404, RpcError(404, "NOT_FOUND", "Project not found"));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_NO_RESULT");
    }

    [Fact]
    public async Task A_quota_worded_429_without_details_that_is_a_per_minute_rate_limit_is_retried()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 429, RpcError(429, "RESOURCE_EXHAUSTED", "Quota exceeded for quota metric 'Requests' and limit 'Requests per minute' of service 'cloudresourcemanager.googleapis.com' for consumer 'project_number:123'."))
            .Returns(Post, "/v3/projects", 200, CreateOperationDone);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(2);
        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_quota_worded_429_without_details_and_without_a_rate_wording_stays_the_project_limit()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, "/v3/projects", 429, RpcError(429, "RESOURCE_EXHAUSTED", "Project creation quota exceeded for this account."));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.ProjectQuota);
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_PERMISSION_DENIED_403_that_mentions_a_constraint_is_still_not_visible_not_an_org_policy_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v3/projects/ghost-lab", 403, RpcError(403, "PERMISSION_DENIED", "Permission denied on resource project ghost-lab. See constraints/iam.allowedPolicyMemberDomains for the policy."));

        (await rig.Gateways.ProjectCatalog.GetProjectAsync("ghost-lab", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_412_org_policy_refusal_when_getting_a_project_still_surfaces()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v3/projects/ghost-lab", 412, RpcError(412, "FAILED_PRECONDITION", "Request blocked by constraints/gcp.restrictServiceUsage."));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.ProjectCatalog.GetProjectAsync("ghost-lab", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("../projects")]
    [InlineData("")]
    [InlineData("Has-Capitals1")]
    [InlineData("short")]
    [InlineData("ends-with-hyphen-")]
    [InlineData("1starts-with-digit")]
    public async Task An_id_that_is_not_a_legal_project_id_is_null_and_sends_nothing(string projectId)
    {
        var rig = new GoogleGatewayHarness();

        (await rig.Gateways.ProjectCatalog.GetProjectAsync(projectId, CancellationToken.None)).ShouldBeNull();

        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rate_limit_without_a_quota_detail_is_retried_not_reported_as_the_project_limit()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 429, RpcError(429, "RESOURCE_EXHAUSTED", "Too many requests, slow down."))
            .Returns(Post, "/v3/projects", 200, CreateOperationDone);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_replayed_create_that_hits_409_returns_the_project_the_first_attempt_made()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 409, RpcError(409, "ALREADY_EXISTS", "Requested entity already exists"))
            .Returns(Get, "/v3/projects/dna-entropy-abcd1234", 200, """{"name":"projects/555","projectId":"dna-entropy-abcd1234","state":"ACTIVE","displayName":"DNA Entropy Graph","labels":{"app":"dna-entropy-graph","installation-id":"inst-1"}}""");

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        project.IsAppProject.ShouldBeTrue();
    }

    [Fact]
    public async Task A_per_minute_rate_limit_is_retried_and_never_reported_as_the_project_limit()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 429, RpcError(429, "RESOURCE_EXHAUSTED", "Quota exceeded for quota metric 'Requests' and limit 'Requests per minute'.", RateLimitDetails))
            .Returns(Post, "/v3/projects", 200, CreateOperationDone);

        var project = await rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        project.ProjectId.ShouldBe("dna-entropy-abcd1234");
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(2);
        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_rate_limit_that_never_clears_ends_as_a_network_error_not_PROJECT_QUOTA()
    {
        var rig = new GoogleGatewayHarness(retries: 2);
        for (var i = 0; i < 3; i++)
        {
            rig.Handler.Returns(Post, "/v3/projects", 429, RpcError(429, "RESOURCE_EXHAUSTED", "Quota exceeded for quota metric 'Requests' and limit 'Requests per minute'.", RateLimitDetails));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldNotBe(SetupErrorCodes.ProjectQuota);
        ex.Kind.ShouldBe(CloudErrorKind.Network);
    }

    [Fact]
    public async Task A_409_for_our_own_project_that_is_being_deleted_is_not_adopted()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 409, RpcError(409, "ALREADY_EXISTS", "Requested entity already exists"))
            .Returns(Get, "/v3/projects/dna-entropy-abcd1234", 200, OurProjectJson.Replace("\"ACTIVE\"", "\"DELETE_REQUESTED\"", StringComparison.Ordinal));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task A_409_for_a_project_that_is_not_ours_is_surfaced_not_adopted()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Post, "/v3/projects", 409, RpcError(409, "ALREADY_EXISTS", "Requested entity already exists"))
            .Returns(Get, "/v3/projects/dna-entropy-abcd1234", 200, """{"name":"projects/9","projectId":"dna-entropy-abcd1234","state":"ACTIVE","displayName":"Someone else"}""");

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task An_operation_that_never_finishes_times_out_as_a_network_error_after_the_deadline()
    {
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromSeconds(3), retries: 0);
        rig.Handler.Returns(Post, "/v3/projects", 200, CreateOperationPending);
        for (var i = 0; i < 10; i++)
        {
            rig.Handler.Returns(Get, "/v3/operations/cp.7001", 200, CreateOperationPending);
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Delays.Sum(d => d.TotalSeconds).ShouldBe(3);
    }

    [Fact]
    public async Task A_create_whose_poll_times_out_posts_exactly_one_create_and_retries_nothing()
    {
        // This proves the gateway makes one POST and never replays it. It does NOT exercise the pipeline's
        // IsTransient guard for the timeout code (no caller runs PollAsync inside the pipeline): that guard is
        // pinned directly in ResilienceRegressionTests.
        var rig = new GoogleGatewayHarness(operationDeadline: TimeSpan.FromSeconds(3), retries: 3);
        rig.Handler.Returns(Post, "/v3/projects", 200, CreateOperationPending);
        for (var i = 0; i < 40; i++)
        {
            rig.Handler.Returns(Get, "/v3/operations/cp.7001", 200, CreateOperationPending);
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.ProjectCatalog.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.To(Post, "/v3/projects").Count.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ the pipeline and the token source

    [Fact]
    public async Task A_401_refreshes_the_token_once_and_replays_the_call_with_the_new_token()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, "/v3/projects:search", 401, RpcError(401, "UNAUTHENTICATED", "Request had invalid authentication credentials."))
            .Returns(Get, "/v3/projects:search", 200, SearchPage2);

        var projects = await rig.Gateways.ProjectCatalog.ListActiveProjectsAsync(CancellationToken.None);

        projects.Count.ShouldBe(1);
        rig.Tokens.Refreshes.ShouldBe(1);
        var calls = rig.Handler.To(Get, "/v3/projects:search");
        calls.Select(c => c.Authorization).ShouldBe(["Bearer ya29.test-token-1", "Bearer ya29.test-token-2"]);
    }

    [Fact]
    public async Task A_refused_sign_in_reaches_the_caller_as_the_auth_error_and_sends_nothing()
    {
        var rig = new GoogleGatewayHarness();
        rig.Tokens.FailWith = new AccountAuthException(AuthErrorCodes.SigninExpired);

        var ex = await Should.ThrowAsync<AccountAuthException>(() => rig.Gateways.ProjectCatalog.ListActiveProjectsAsync(CancellationToken.None));

        ex.Code.ShouldBe(AuthErrorCodes.SigninExpired);
        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Server_errors_are_retried_and_then_given_up_as_a_network_error()
    {
        var rig = new GoogleGatewayHarness(retries: 2);
        for (var i = 0; i < 3; i++)
        {
            rig.Handler.Returns(Get, "/v3/projects:search", 503, RpcError(503, "UNAVAILABLE", "The service is currently unavailable."));
        }

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.ProjectCatalog.ListActiveProjectsAsync(CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        rig.Handler.To(Get, "/v3/projects:search").Count.ShouldBe(3);
        rig.Log.Retries.Count.ShouldBe(2);
    }
}
