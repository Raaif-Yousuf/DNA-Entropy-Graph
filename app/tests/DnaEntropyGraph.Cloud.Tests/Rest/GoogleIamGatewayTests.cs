using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #54: the real worker-identity gateway (IAM v1 service account and custom role, the project IAM policy through
/// Resource Manager v3, the bucket IAM policy through Cloud Storage v1) over a scripted HTTP handler. Payloads are
/// Google's documented REST shapes, not captured from a live project: there is no GCP access in this build. The
/// decisive observables are the setIamPolicy bodies: a version-3 policy, the worker role bound to the worker account with
/// the deg- name condition, and every binding that was already there still there.
/// </summary>
public class GoogleIamGatewayTests
{
    private const string Project = "my-lab";
    private const string Number = "123456789012";
    private const string Bucket = "deg-123456789012-abcdef";
    private static readonly string WorkerEmail = WorkerIdentityNames.ServiceAccountEmail(Project);
    private static readonly string DefaultEmail = WorkerIdentityNames.DefaultComputeAccountEmail(Number);

    private const string ProjectPath = "/v3/projects/my-lab";
    private const string ProjectGetPolicy = "/v3/projects/my-lab:getIamPolicy";
    private const string ProjectSetPolicy = "/v3/projects/my-lab:setIamPolicy";
    private const string Accounts = "/v1/projects/my-lab/serviceAccounts";
    private const string Roles = "/v1/projects/my-lab/roles";
    private const string RolePath = "/v1/projects/my-lab/roles/dnaEntropyWorker";
    private const string BucketPolicy = "/storage/v1/b/" + Bucket + "/iam";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Patch = HttpMethod.Patch;
    private static readonly HttpMethod Put = HttpMethod.Put;

    private static string AccountPath(string email) => Accounts + "/" + email.Replace("@", "%40");

    private static string RpcError(int http, string status, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"}}";

    private static string ProjectBody() => "{\"name\":\"projects/" + Number + "\",\"projectId\":\"my-lab\",\"state\":\"ACTIVE\",\"displayName\":\"Lab\"}";

    private static string AccountBody(string email) => "{\"name\":\"projects/my-lab/serviceAccounts/" + email + "\",\"email\":\"" + email + "\",\"uniqueId\":\"1042\"}";

    private static string RoleBody(IEnumerable<string>? permissions = null, bool deleted = false)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = "projects/my-lab/roles/dnaEntropyWorker",
            ["title"] = "DNA Entropy Graph worker",
            ["includedPermissions"] = (permissions ?? WorkerIdentityNames.RolePermissions).ToArray(),
            ["stage"] = "GA",
            ["etag"] = "BwZ1",
            ["deleted"] = deleted,
        });

    private static string PolicyBody(string etag, string bindings, int version = 1)
        => "{\"version\":" + version + ",\"etag\":\"" + etag + "\",\"bindings\":[" + bindings + "]}";

    private const string OwnerBinding = "{\"role\":\"roles/owner\",\"members\":[\"user:lab@example.org\"]}";

    private static string WorkerBinding(string email, string? role = null)
        => "{\"role\":\"" + (role ?? WorkerIdentityNames.ProjectRoleName(Project)) + "\",\"members\":[\"serviceAccount:" + email + "\"],"
            + "\"condition\":{\"title\":\"" + WorkerIdentityNames.ConditionTitle + "\",\"expression\":" + JsonSerializer.Serialize(WorkerIdentityNames.ConditionExpression) + "}}";

    private static string BucketBinding(string email)
        => "{\"role\":\"roles/storage.objectAdmin\",\"members\":[\"serviceAccount:" + email + "\"]}";

    /// <summary>The creating run's first half: the project is visible, the account and role are made.</summary>
    private static void ScriptCreate(ScriptedHttpHandler handler, string email)
    {
        handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 200, AccountBody(email))
            .Returns(Post, Roles, 200, RoleBody());
    }

    private static JsonElement PolicyOf(RecordedRequest request)
    {
        var root = JsonDocument.Parse(request.Body).RootElement;
        return root.TryGetProperty("policy", out var wrapped) ? wrapped.Clone() : root.Clone();
    }

    private static IReadOnlyList<JsonElement> Bindings(JsonElement policy, string role)
        => policy.GetProperty("bindings").EnumerateArray().Where(b => b.GetProperty("role").GetString() == role).ToList();

    [Fact]
    public async Task A_new_project_gets_the_account_the_role_a_version_3_conditional_binding_and_the_bucket_binding()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        rig.Handler
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwOwn", OwnerBinding))
            .Returns(Post, ProjectSetPolicy, 200, PolicyBody("BwNew", OwnerBinding + "," + WorkerBinding(WorkerEmail), 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", "{\"role\":\"roles/storage.legacyBucketOwner\",\"members\":[\"projectOwner:my-lab\"]}"))
            .Returns(Put, BucketPolicy, 200, PolicyBody("CAI=", "", 3));

        var identity = await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        identity.ServiceAccountEmail.ShouldBe(WorkerEmail);
        identity.NoteCode.ShouldBeNull();

        // The account.
        var account = JsonDocument.Parse(rig.Handler.To(Post, Accounts).Single().Body).RootElement;
        account.GetProperty("accountId").GetString().ShouldBe("dna-entropy-worker");
        rig.Handler.To(Post, Accounts).Single().Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);

        // The role: exactly the four permissions, nothing more.
        var role = JsonDocument.Parse(rig.Handler.To(Post, Roles).Single().Body).RootElement;
        role.GetProperty("roleId").GetString().ShouldBe("dnaEntropyWorker");
        role.GetProperty("role").GetProperty("includedPermissions").EnumerateArray().Select(e => e.GetString()).ShouldBe(
            ["compute.instances.delete", "compute.instances.get", "compute.instances.stop", "compute.zoneOperations.get"], ignoreOrder: true);

        // Conditional bindings need policy version 3 on the read AND the write, or the condition is dropped or refused.
        var read = JsonDocument.Parse(rig.Handler.To(Post, ProjectGetPolicy).Single().Body).RootElement;
        read.GetProperty("options").GetProperty("requestedPolicyVersion").GetInt32().ShouldBe(3);

        var written = PolicyOf(rig.Handler.To(Post, ProjectSetPolicy).Single());
        written.GetProperty("version").GetInt32().ShouldBe(3);
        written.GetProperty("etag").GetString().ShouldBe("BwOwn");
        Bindings(written, "roles/owner").Single().GetProperty("members").EnumerateArray().Select(e => e.GetString()).ShouldBe(["user:lab@example.org"]);

        var worker = Bindings(written, "projects/my-lab/roles/dnaEntropyWorker").Single();
        worker.GetProperty("members").EnumerateArray().Select(e => e.GetString()).ShouldBe(["serviceAccount:" + WorkerEmail]);
        var condition = worker.GetProperty("condition");
        condition.GetProperty("expression").GetString().ShouldBe(WorkerIdentityNames.ConditionExpression);
        condition.GetProperty("expression").GetString()!.ShouldContain("/instances/deg-");
        condition.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();

        // The bucket: objectAdmin on this bucket only, the existing binding kept.
        rig.Handler.To(Put, BucketPolicy).Count.ShouldBe(1);
        var bucket = PolicyOf(rig.Handler.To(Put, BucketPolicy).Single());
        Bindings(bucket, "roles/storage.legacyBucketOwner").Count.ShouldBe(1);
        var objectAdmin = Bindings(bucket, "roles/storage.objectAdmin").Single();
        objectAdmin.GetProperty("members").EnumerateArray().Select(e => e.GetString()).ShouldBe(["serviceAccount:" + WorkerEmail]);
        objectAdmin.TryGetProperty("condition", out _).ShouldBeFalse();
        bucket.GetProperty("etag").GetString().ShouldBe("CAE=");
        rig.Handler.Requests.Select(r => r.Uri.AbsolutePath).ShouldNotContain(p => p.Contains("/b/") && !p.StartsWith("/storage/v1/b/" + Bucket, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rerun_over_everything_already_in_place_adds_nothing()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 409, RpcError(409, "ALREADY_EXISTS", "Service account dna-entropy-worker already exists within project projects/my-lab."))
            .Returns(Get, AccountPath(WorkerEmail), 200, AccountBody(WorkerEmail))
            .Returns(Post, Roles, 409, RpcError(409, "ALREADY_EXISTS", "The role named dnaEntropyWorker already exists."))
            .Returns(Get, RolePath, 200, RoleBody())
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", OwnerBinding + "," + WorkerBinding(WorkerEmail), 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", BucketBinding(WorkerEmail), 1));

        var identity = await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        identity.ServiceAccountEmail.ShouldBe(WorkerEmail);
        rig.Handler.To(Post, ProjectSetPolicy).ShouldBeEmpty("the binding is already there: a second run must not write the policy");
        rig.Handler.To(Put, BucketPolicy).ShouldBeEmpty();
        rig.Handler.To(Patch, RolePath).ShouldBeEmpty("the role already has exactly the right permissions");
        rig.Handler.To(Post, RolePath + ":undelete").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_role_binding_with_somebody_elses_condition_is_left_alone_and_ours_is_added_beside_it()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        var theirs = "{\"role\":\"projects/my-lab/roles/dnaEntropyWorker\",\"members\":[\"user:admin@example.org\"],\"condition\":{\"title\":\"theirs\",\"expression\":\"true\"}}";
        rig.Handler
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", theirs, 3))
            .Returns(Post, ProjectSetPolicy, 200, PolicyBody("BwB", theirs, 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", BucketBinding(WorkerEmail)));

        await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        var written = PolicyOf(rig.Handler.To(Post, ProjectSetPolicy).Single());
        var bindings = Bindings(written, "projects/my-lab/roles/dnaEntropyWorker");
        bindings.Count.ShouldBe(2);
        bindings.Single(b => b.GetProperty("condition").GetProperty("title").GetString() == "theirs").GetProperty("members").EnumerateArray().Select(e => e.GetString()).ShouldBe(["user:admin@example.org"]);
        bindings.Single(b => b.GetProperty("condition").GetProperty("expression").GetString() == WorkerIdentityNames.ConditionExpression)
            .GetProperty("members").EnumerateArray().Select(e => e.GetString()).ShouldBe(["serviceAccount:" + WorkerEmail]);
    }

    [Fact]
    public async Task A_binding_that_exists_without_our_member_gets_the_member_added_and_keeps_its_others()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        var shared = "{\"role\":\"roles/storage.objectAdmin\",\"members\":[\"user:lab@example.org\"]}";
        rig.Handler
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", WorkerBinding(WorkerEmail), 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", shared))
            .Returns(Put, BucketPolicy, 200, PolicyBody("CAI=", shared));

        await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        var bucket = PolicyOf(rig.Handler.To(Put, BucketPolicy).Single());
        Bindings(bucket, "roles/storage.objectAdmin").Single().GetProperty("members").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["user:lab@example.org", "serviceAccount:" + WorkerEmail]);
    }

    [Fact]
    public async Task A_deleted_role_that_keeps_its_id_is_undeleted_not_failed()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 200, AccountBody(WorkerEmail))
            .Returns(Post, Roles, 409, RpcError(409, "ALREADY_EXISTS", "You can't create a role with the id dnaEntropyWorker: it was deleted recently."))
            .Returns(Get, RolePath, 200, RoleBody(deleted: true))
            .Returns(Post, RolePath + ":undelete", 200, RoleBody())
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", WorkerBinding(WorkerEmail), 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", BucketBinding(WorkerEmail)));

        await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        rig.Handler.To(Post, RolePath + ":undelete").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_role_with_the_wrong_permissions_is_patched_and_read_back()
    {
        var rig = new GoogleGatewayHarness();
        var extra = WorkerIdentityNames.RolePermissions.Append("compute.instances.setMetadata");
        rig.Handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 200, AccountBody(WorkerEmail))
            .Returns(Post, Roles, 409, RpcError(409, "ALREADY_EXISTS", "The role named dnaEntropyWorker already exists."))
            .Returns(Get, RolePath, 200, RoleBody(extra))
            .Returns(Patch, RolePath, 200, RoleBody())
            .Returns(Get, RolePath, 200, RoleBody())
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", WorkerBinding(WorkerEmail), 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", BucketBinding(WorkerEmail)));

        await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        var patch = rig.Handler.To(Patch, RolePath).Single();
        patch.Uri.Query.ShouldContain("updateMask=includedPermissions");
        JsonDocument.Parse(patch.Body).RootElement.GetProperty("includedPermissions").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(WorkerIdentityNames.RolePermissions, ignoreOrder: true);
    }

    [Fact]
    public async Task A_patch_that_does_not_read_back_as_asked_fails_with_the_named_code_and_binds_nothing()
    {
        var rig = new GoogleGatewayHarness();
        var extra = WorkerIdentityNames.RolePermissions.Append("compute.instances.setMetadata");
        rig.Handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 200, AccountBody(WorkerEmail))
            .Returns(Post, Roles, 409, RpcError(409, "ALREADY_EXISTS", "The role named dnaEntropyWorker already exists."))
            .Returns(Get, RolePath, 200, RoleBody(extra))
            .Returns(Patch, RolePath, 200, RoleBody())
            .Returns(Get, RolePath, 200, RoleBody(extra));

        var failure = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Error.Code.ShouldBe(SetupErrorCodes.WorkerIdentityNotApplied);
        rig.Handler.To(Post, ProjectSetPolicy).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_org_policy_that_forbids_service_accounts_falls_back_to_the_default_compute_account_with_the_same_bindings()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 400, RpcError(400, "FAILED_PRECONDITION", "Service account creation is not allowed on this project. Operation denied by org policy: [constraints/iam.disableServiceAccountCreation]."))
            .Returns(Post, Roles, 200, RoleBody())
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwOwn", OwnerBinding))
            .Returns(Post, ProjectSetPolicy, 200, PolicyBody("BwNew", OwnerBinding, 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", ""))
            .Returns(Put, BucketPolicy, 200, PolicyBody("CAI=", ""));

        var identity = await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        identity.ServiceAccountEmail.ShouldBe(DefaultEmail);
        identity.NoteCode.ShouldBe(SetupErrorCodes.WorkerDefaultAccount);
        identity.IsDefaultComputeAccount.ShouldBeTrue();

        var written = PolicyOf(rig.Handler.To(Post, ProjectSetPolicy).Single());
        written.GetProperty("version").GetInt32().ShouldBe(3);
        var worker = Bindings(written, "projects/my-lab/roles/dnaEntropyWorker").Single();
        worker.GetProperty("members").EnumerateArray().Select(e => e.GetString()).ShouldBe(["serviceAccount:" + DefaultEmail]);
        worker.GetProperty("condition").GetProperty("expression").GetString().ShouldBe(WorkerIdentityNames.ConditionExpression);

        var bucket = PolicyOf(rig.Handler.To(Put, BucketPolicy).Single());
        Bindings(bucket, "roles/storage.objectAdmin").Single().GetProperty("members").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["serviceAccount:" + DefaultEmail]);
    }

    [Fact]
    public async Task A_plain_permission_refusal_creating_the_account_is_not_an_org_policy_and_does_not_fall_back()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 403, RpcError(403, "PERMISSION_DENIED", "Permission 'iam.serviceAccounts.create' denied on resource (or it may not exist)."));

        var failure = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.To(Post, ProjectSetPolicy).ShouldBeEmpty();
        rig.Handler.To(Post, Roles).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusal_to_act_as_the_account_carries_the_PERMISSION_ACTAS_code_and_stays_a_permission_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, ProjectPath, 200, ProjectBody())
            .Returns(Post, Accounts, 403, RpcError(403, "PERMISSION_DENIED", "Required 'iam.serviceAccounts.actAs' permission for 'projects/my-lab/serviceAccounts/" + WorkerEmail + "'"));

        var failure = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Kind.ShouldBe(CloudErrorKind.Permission);
        failure.Error.Code.ShouldBe(SetupErrorCodes.PermissionActAs);
    }

    [Fact]
    public async Task A_concurrent_writer_409_re_reads_the_policy_and_keeps_the_binding_that_slipped_in()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        var theirs = "{\"role\":\"roles/viewer\",\"members\":[\"user:new@example.org\"]}";
        rig.Handler
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwOld", OwnerBinding))
            .Returns(Post, ProjectSetPolicy, 409, RpcError(409, "ABORTED", "There were concurrent policy changes. Please retry the whole read-modify-write with exponential backoff."))
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwFresh", OwnerBinding + "," + theirs))
            .Returns(Post, ProjectSetPolicy, 200, PolicyBody("BwDone", OwnerBinding, 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", BucketBinding(WorkerEmail)));

        await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        var writes = rig.Handler.To(Post, ProjectSetPolicy);
        writes.Count.ShouldBe(2);
        var second = PolicyOf(writes[1]);
        second.GetProperty("etag").GetString().ShouldBe("BwFresh");
        Bindings(second, "roles/viewer").Count.ShouldBe(1, "the other writer's binding must survive our retry");
        Bindings(second, "projects/my-lab/roles/dnaEntropyWorker").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_conflict_that_never_clears_stops_after_a_bounded_number_of_attempts()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        for (var i = 0; i < 10; i++)
        {
            rig.Handler
                .Returns(Post, ProjectGetPolicy, 200, PolicyBody("Bw" + i, OwnerBinding))
                .Returns(Post, ProjectSetPolicy, 409, RpcError(409, "ABORTED", "There were concurrent policy changes."));
        }

        var failure = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Error.Code.ShouldBe(SetupErrorCodes.WorkerIdentityNotApplied);
        var writes = rig.Handler.To(Post, ProjectSetPolicy).Count;
        writes.ShouldBeInRange(2, 6);
    }

    [Fact]
    public async Task A_just_created_account_that_the_policy_cannot_see_yet_is_waited_for_on_the_injected_clock_then_bound()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        var unseen = RpcError(400, "INVALID_ARGUMENT", "Service account " + WorkerEmail + " does not exist.");
        rig.Handler
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", OwnerBinding))
            .Returns(Post, ProjectSetPolicy, 400, unseen)
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", OwnerBinding))
            .Returns(Post, ProjectSetPolicy, 400, unseen)
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", OwnerBinding))
            .Returns(Post, ProjectSetPolicy, 200, PolicyBody("BwB", OwnerBinding, 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", BucketBinding(WorkerEmail)));

        await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        rig.Handler.To(Post, ProjectSetPolicy).Count.ShouldBe(3);
        rig.Delays.Count.ShouldBe(2, "each wait goes through the injected delay, never a real sleep");
        rig.Delays.ShouldAllBe(d => d > TimeSpan.Zero);
    }

    [Fact]
    public async Task An_account_that_never_becomes_visible_fails_with_the_named_code_after_a_bounded_wait()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        var unseen = RpcError(400, "INVALID_ARGUMENT", "Service account " + WorkerEmail + " does not exist.");
        for (var i = 0; i < 12; i++)
        {
            rig.Handler.Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", OwnerBinding)).Returns(Post, ProjectSetPolicy, 400, unseen);
        }

        var failure = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Error.Code.ShouldBe(SetupErrorCodes.WorkerIdentityNotApplied);
        rig.Delays.Count.ShouldBeInRange(1, 8);
    }

    [Fact]
    public async Task A_transient_503_reading_the_policy_is_retried_by_the_pipeline_and_the_account_is_still_created_once()
    {
        var rig = new GoogleGatewayHarness();
        ScriptCreate(rig.Handler, WorkerEmail);
        rig.Handler
            .Returns(Post, ProjectGetPolicy, 503, RpcError(503, "UNAVAILABLE", "The service is currently unavailable."))
            .Returns(Post, ProjectGetPolicy, 200, PolicyBody("BwA", WorkerBinding(WorkerEmail), 3))
            .Returns(Get, BucketPolicy, 200, PolicyBody("CAE=", BucketBinding(WorkerEmail)));

        await rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        rig.Handler.To(Post, ProjectGetPolicy).Count.ShouldBe(2);
        rig.Handler.To(Post, Accounts).Count.ShouldBe(1);
        rig.Handler.To(Post, Roles).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_project_the_account_cannot_see_fails_before_anything_is_created()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, ProjectPath, 404, RpcError(404, "NOT_FOUND", "Project not found."));

        var failure = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Gateways.WorkerIdentity.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.To(Post, Accounts).ShouldBeEmpty();
    }

    [Fact]
    public void The_condition_names_the_deg_prefix_VmSpec_gives_every_VM()
    {
        WorkerIdentityNames.VmNamePrefix.ShouldBe("deg-");
        new VmSpec("my-lab", "install-1", "20260919-084512-ab23cd", "evo2_7b", "0.1.0", "run", "g2-standard-8", TimeSpan.FromHours(4), "DELETE")
            .VmName.ShouldStartWith(WorkerIdentityNames.VmNamePrefix);
        WorkerIdentityNames.ConditionExpression.ShouldContain("\"/instances/" + WorkerIdentityNames.VmNamePrefix + "\"");
        WorkerIdentityNames.RolePermissions.ShouldBe(
            ["compute.instances.delete", "compute.instances.get", "compute.instances.stop", "compute.zoneOperations.get"]);
    }
}
