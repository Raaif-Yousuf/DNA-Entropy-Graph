using System.Text.Json;
using DnaEntropyGraph.Cloud.Tests.Rest;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #604: a refusal to run a VM as the worker account (PERMISSION_ACTAS) and the worker-identity step's own setup
/// refusals reach the run row under their own code, so the Runs page shows their message and their one action instead of
/// the generic "permission". The observable that differs when this is wired to nothing is RunRecord.ErrorCode.
/// </summary>
public class ActAsRunRowTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";
    private const string ActAsMessage = "Required 'iam.serviceAccounts.actAs' permission for 'projects/my-project/serviceAccounts/dna-entropy-worker@my-project.iam.gserviceaccount.com'";
    private const string EmptyAggregate = "{\"kind\":\"compute#instanceAggregatedList\",\"items\":{}}";

    private static VmSpec Spec(string jobId) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudRunSettings Settings() => new()
    {
        CreateTimeout = TimeSpan.FromSeconds(30),
        CreateSettleTimeout = TimeSpan.FromMilliseconds(100),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(50),
    };

    private static CloudJobRunner Runner(IComputeGateway compute, FakeGcp gcp, InMemoryRunRepository repo)
        => new(compute, gcp, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            // Generous safety values: no assertion here depends on a real-time window, so a stalled CI machine must not turn a
            // refusal into a `network` timeout.
            CallTimeout = TimeSpan.FromSeconds(30),
            CreateTimeout = TimeSpan.FromSeconds(30),
            CreateSettleTimeout = TimeSpan.FromMilliseconds(100),
            UploadTimeout = TimeSpan.FromSeconds(30),
            BootTimeout = TimeSpan.FromSeconds(30),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    public static TheoryData<string?, int?, string, CloudErrorKind, string> ErrorsAndTheirRowCodes() => new()
    {
        { SetupErrorCodes.PermissionActAs, 403, "anything", CloudErrorKind.Permission, RunErrorCodes.PermissionActAs },
        { "IAM_PERMISSION_DENIED", 403, ActAsMessage, CloudErrorKind.Permission, RunErrorCodes.PermissionActAs },
        { "IAM_PERMISSION_DENIED", 403, "Required 'compute.instances.create' permission", CloudErrorKind.Permission, "permission" },
        { "IAM_PERMISSION_DENIED", 500, ActAsMessage, CloudErrorKind.Permission, "permission" },
        { SetupErrorCodes.WorkerDefaultAccountMissing, 403, "gone", CloudErrorKind.Permission, RunErrorCodes.WorkerDefaultAccountMissing },
        { SetupErrorCodes.WorkerIdentityNotApplied, null, "not applied", CloudErrorKind.Other, RunErrorCodes.WorkerIdentityNotApplied },
        { "ZONE_RESOURCE_POOL_EXHAUSTED", 503, "no capacity", CloudErrorKind.Stockout, "stockout" },
        { null, null, "who knows", CloudErrorKind.Other, "other" },
    };

    [Theory]
    [MemberData(nameof(ErrorsAndTheirRowCodes))]
    public void A_cloud_error_maps_to_its_own_run_code_when_it_has_one_and_to_its_kind_otherwise(string? code, int? http, string message, CloudErrorKind kind, string expected)
    {
        ErrorsAndTheirRowCodes().Count.ShouldBeGreaterThanOrEqualTo(8);

        RunErrorCodes.For(new CloudError(code, http, message), kind).ShouldBe(expected);
    }

    [Fact]
    public void Every_specific_run_code_is_a_recorded_code()
    {
        RunErrorCodes.All.ShouldContain(RunErrorCodes.PermissionActAs);
        RunErrorCodes.All.ShouldContain(RunErrorCodes.WorkerDefaultAccountMissing);
        RunErrorCodes.All.ShouldContain(RunErrorCodes.WorkerIdentityNotApplied);
    }

    [Fact]
    public async Task A_polled_create_operation_naming_actAs_reaches_the_provisioner_result_as_PERMISSION_ACTAS()
    {
        var gcp = new FakeGcp();
        var compute = new RefusingCreate(gcp, new CloudOperationException(new CloudError("IAM_PERMISSION_DENIED", 403, ActAsMessage), CloudErrorKind.Permission));
        var settings = Settings();
        var calls = new GatewayCalls(settings, gcp);
        var provisioner = new VmProvisioner(compute, calls, settings, new VmTerminator(compute, calls, settings));
        var spec = Spec("job-a1");

        var result = await provisioner.ProvisionAsync(TestInputs.Request("job-a1", spec, zones: [Zone, "us-central1-b"]), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Permission);
        result.FailureCode.ShouldBe(RunErrorCodes.PermissionActAs);
        compute.CreateAttempts.ShouldBe(1, "a permission refusal is project-wide: no second zone");
    }

    [Fact]
    public async Task A_VM_create_refused_for_actAs_ends_the_run_row_as_PERMISSION_ACTAS()
    {
        var gcp = new FakeGcp();
        var compute = new RefusingCreate(gcp, new CloudOperationException(new CloudError("IAM_PERMISSION_DENIED", 403, ActAsMessage), CloudErrorKind.Permission));
        var repo = new InMemoryRunRepository();

        var result = await Runner(compute, gcp, repo).RunAsync(TestInputs.Request("job-a2", Spec("job-a2")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.PermissionActAs);
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-a2").ErrorCode.ShouldBe(RunErrorCodes.PermissionActAs);
    }

    [Fact]
    public async Task Through_the_real_insert_path_an_operation_error_naming_actAs_is_recorded_on_the_row_as_PERMISSION_ACTAS()
    {
        const string Base = "/compute/v1/projects/" + Project;
        const string Instances = Base + "/zones/" + Zone + "/instances";
        const string Aggregated = Base + "/aggregated/instances";
        // A clock that never moves: the operation poll's deadline cannot fire, and the poll's waits are the harness's instant ones.
        var rig = new GoogleGatewayHarness(selectedProjectId: Project, time: new VirtualTimeProvider());
        var failed = "{\"kind\":\"compute#operation\",\"name\":\"operation-123\",\"operationType\":\"insert\",\"status\":\"DONE\",\"httpErrorStatusCode\":403,"
            + "\"error\":{\"errors\":[{\"code\":\"IAM_PERMISSION_DENIED\",\"message\":" + JsonSerializer.Serialize(ActAsMessage) + "}]}}";
        rig.Handler
            .Returns(HttpMethod.Get, Aggregated, 200, EmptyAggregate)
            .Returns(HttpMethod.Post, Instances, 200, "{\"kind\":\"compute#operation\",\"name\":\"operation-123\",\"operationType\":\"insert\",\"status\":\"PENDING\"}")
            .Returns(HttpMethod.Get, Base + "/zones/" + Zone + "/operations/operation-123", 200, failed)
            .Returns(HttpMethod.Get, Aggregated, 200, EmptyAggregate)
            .Returns(HttpMethod.Get, Aggregated, 200, EmptyAggregate);
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();

        var result = await Runner(rig.Gateways.Compute, gcp, repo).RunAsync(TestInputs.Request("job-a3", Spec("job-a3")), CancellationToken.None);

        rig.Handler.To(HttpMethod.Post, Instances).Count.ShouldBe(1, "the real insert path was exercised");
        result.FinalPhase.ShouldBe(JobPhase.Failed);
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-a3").ErrorCode.ShouldBe(RunErrorCodes.PermissionActAs);
    }

    [Fact]
    public async Task An_actAs_refusal_from_the_identity_step_is_recorded_as_PERMISSION_ACTAS_and_creates_no_VM()
    {
        var gcp = new FakeGcp().WithWorkerIdentityActAsDenied();
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-a4", Spec("job-a4")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Permission);
        result.FailureCode.ShouldBe(RunErrorCodes.PermissionActAs);
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-a4").ErrorCode.ShouldBe(RunErrorCodes.PermissionActAs);
        gcp.CreatedSpecs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_default_account_from_the_identity_step_is_recorded_under_its_own_code()
    {
        var gcp = new FakeGcp().WithDefaultComputeAccountMissing();
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-a5", Spec("job-a5")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerDefaultAccountMissing);
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-a5").ErrorCode.ShouldBe(RunErrorCodes.WorkerDefaultAccountMissing);
        gcp.CreatedSpecs.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_row_names_the_worker_account_the_VM_was_to_run_as()
    {
        var gcp = new FakeGcp();
        var compute = new RefusingCreate(gcp, new CloudOperationException(new CloudError("IAM_PERMISSION_DENIED", 403, ActAsMessage), CloudErrorKind.Permission));
        var repo = new InMemoryRunRepository();

        await Runner(compute, gcp, repo).RunAsync(TestInputs.Request("job-a6", Spec("job-a6")), CancellationToken.None);

        WorkerAccountDetail.TryRead(repo.AllRecordedInOrder.Last(r => r.JobId == "job-a6").ErrorDetail).ShouldBe(WorkerIdentityNames.ServiceAccountEmail(Project));
    }

    [Fact]
    public async Task When_an_org_policy_made_the_VM_run_as_the_default_compute_account_the_row_names_that_account()
    {
        var gcp = new FakeGcp().WithServiceAccountCreationBlockedByOrgPolicy();
        var compute = new RefusingCreate(gcp, new CloudOperationException(new CloudError("IAM_PERMISSION_DENIED", 403, ActAsMessage), CloudErrorKind.Permission));
        var repo = new InMemoryRunRepository();

        await Runner(compute, gcp, repo).RunAsync(TestInputs.Request("job-a7", Spec("job-a7")), CancellationToken.None);

        var row = repo.AllRecordedInOrder.Last(r => r.JobId == "job-a7");
        row.ErrorCode.ShouldBe(RunErrorCodes.PermissionActAs);
        WorkerAccountDetail.TryRead(row.ErrorDetail).ShouldBe(WorkerIdentityNames.DefaultComputeAccountEmail(FakeGcp.ProjectNumberFor(Project)));
    }

    [Fact]
    public void An_unconfirmed_VM_end_keeps_the_specific_original_code_in_its_detail()
    {
        var refusal = new CloudOperationException(new CloudError("IAM_PERMISSION_DENIED", 403, ActAsMessage), CloudErrorKind.Permission);

        var ex = VmEndNotes.Apply(refusal, " (VM end not confirmed)");

        ex.Message.ShouldContain("Original code: permission_actas.");
    }

    [Theory]
    [InlineData("denied", "a@b.iam.gserviceaccount.com", "denied [vm-account=a@b.iam.gserviceaccount.com]")]
    [InlineData(null, "a@b.c", "[vm-account=a@b.c]")]
    [InlineData("denied", null, "denied")]
    [InlineData("denied", " ", "denied")]
    public void The_account_mark_is_appended_only_when_known(string? detail, string? email, string expected)
        => WorkerAccountDetail.Append(detail, email).ShouldBe(expected);

    [Theory]
    [InlineData("denied [vm-account=a@b.iam.gserviceaccount.com]", "a@b.iam.gserviceaccount.com")]
    [InlineData("msg [vm-account=a@b] [vm-account=c@d]", "c@d")]
    [InlineData("no mark here", null)]
    [InlineData("[vm-account=not an email]", null)]
    [InlineData(null, null)]
    public void The_account_mark_reads_back_only_when_well_formed(string? detail, string? expected)
        => WorkerAccountDetail.TryRead(detail).ShouldBe(expected);

    /// <summary>Compute whose create always fails with the scripted error; everything else is the fake's.</summary>
    private sealed class RefusingCreate(FakeGcp inner, CloudOperationException refusal) : IComputeGateway
    {
        public int CreateAttempts { get; private set; }

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
        {
            CreateAttempts++;
            return Task.FromException<VmDescriptor>(refusal);
        }

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);
    }
}
