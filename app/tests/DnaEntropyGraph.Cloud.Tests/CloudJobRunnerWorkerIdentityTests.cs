using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #606: the runner ensures the worker identity (issue #54) after the results bucket exists and before any VM is
/// created, and hands the account it returns to the VM. The observable that differs when this is wired to nothing is the
/// spec the compute gateway receives: no service account on it, and no call on the identity gateway.
/// </summary>
public class CloudJobRunnerWorkerIdentityTests
{
    private const string Project = "my-project";

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

    private static CloudJobRunner Runner(FakeGcp gcp, IWorkerIdentityGateway identity, InMemoryRunRepository repo)
        => new(gcp, gcp, gcp, gcp, identity, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            CallTimeout = TimeSpan.FromSeconds(1),
            CreateTimeout = TimeSpan.FromSeconds(1),
            CreateSettleTimeout = TimeSpan.FromMilliseconds(100),
            UploadTimeout = TimeSpan.FromSeconds(1),
            BootTimeout = TimeSpan.FromSeconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    [Fact]
    public async Task The_identity_is_ensured_once_with_the_bucket_before_the_create_and_its_email_reaches_the_VM_spec()
    {
        var gcp = new FakeGcp();
        var recorder = new RecordingIdentity(gcp, () => gcp.CreatedSpecs.Count);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, recorder, repo).RunAsync(TestInputs.Request("job-i1", Spec("job-i1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.WorkerIdentityEnsureCalls.ShouldBe(1);
        recorder.Calls.Count.ShouldBe(1);
        recorder.Calls[0].Project.ShouldBe(Project);
        recorder.Calls[0].Bucket.ShouldBe(await gcp.EnsureBucketAsync(Project, CancellationToken.None));
        recorder.Calls[0].VmsCreatedSoFar.ShouldBe(0, "the identity must exist before the first VM create");
        gcp.CreatedSpecs.ShouldNotBeEmpty();
        gcp.CreatedSpecs.ShouldAllBe(spec => spec.ServiceAccountEmail == WorkerIdentityNames.ServiceAccountEmail(Project));
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-i1").ErrorCode.ShouldBeNull("no note when the app's own account is used");
    }

    [Fact]
    public async Task The_default_account_note_does_not_stop_the_run_and_is_recorded_on_the_row()
    {
        var gcp = new FakeGcp().WithServiceAccountCreationBlockedByOrgPolicy();
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-i2", Spec("job-i2")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.ShouldAllBe(spec => spec.ServiceAccountEmail == WorkerIdentityNames.DefaultComputeAccountEmail(FakeGcp.ProjectNumberFor(Project)));
        var row = repo.AllRecordedInOrder.Last(r => r.JobId == "job-i2");
        row.Phase.ShouldBe(JobPhase.Completed);
        row.ErrorCode.ShouldBe(RunErrorCodes.WorkerDefaultAccount);
    }

    [Fact]
    public async Task A_failed_identity_step_creates_no_VM_and_ends_the_run_classified()
    {
        var gcp = new FakeGcp().WithWorkerIdentityPermissionDenied();
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-i3", Spec("job-i3")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Permission);
        result.FailureCode.ShouldBe("permission");
        gcp.CreatedSpecs.ShouldBeEmpty();
        (await gcp.FindByJobIdAsync("job-i3", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_finished_job_asked_to_run_again_does_not_touch_the_identity()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var first = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-i4", Spec("job-i4")), CancellationToken.None);
        first.FinalPhase.ShouldBe(JobPhase.Completed, first.FailureMessage);

        var again = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-i4", Spec("job-i4")), CancellationToken.None);

        again.FinalPhase.ShouldBe(JobPhase.Completed);
        gcp.WorkerIdentityEnsureCalls.ShouldBe(1, "a terminal job is returned from its row, nothing is re-ensured");
    }

    private sealed class RecordingIdentity(IWorkerIdentityGateway inner, Func<int> vmsCreated) : IWorkerIdentityGateway
    {
        public List<(string Project, string Bucket, int VmsCreatedSoFar)> Calls { get; } = [];

        public Task<WorkerIdentity> EnsureWorkerIdentityAsync(string projectId, string bucket, CancellationToken cancellationToken)
        {
            Calls.Add((projectId, bucket, vmsCreated()));
            return inner.EnsureWorkerIdentityAsync(projectId, bucket, cancellationToken);
        }
    }
}
