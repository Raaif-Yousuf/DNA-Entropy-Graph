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
    {
        // Test time (#567): every deadline is on a clock that moves only as the runner polls (80 ms of real time under load cut a healthy run).
        var clocks = new RunnerClocks();
        return new(gcp, gcp, gcp, gcp, identity, repo)
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
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };
    }

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

    [Fact]
    public async Task A_resume_that_adopts_an_existing_VM_never_asks_for_the_identity_so_an_IAM_failure_cannot_end_it()
    {
        // The cold review of #606: adoption needed no IAM call before this change, and a failing one must not stop a working VM.
        var gcp = new FakeGcp().WithWorkerIdentityPermissionDenied();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-i5", Spec("job-i5"));
        await gcp.CreateVmAsync(request.Spec, "us-central1-a", CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-i5", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await Runner(gcp, gcp, repo).RunAsync(request, CancellationToken.None);

        // Whatever else the resumed run does (the manifest was never uploaded here, so it waits for a result and times out),
        // it is not the identity refusal, and the adopted VM was looked at, never asked about IAM.
        result.FailureCode.ShouldBe(RunErrorCodes.ResultTimeout, "it got through provisioning (adopting the VM) into the wait for the result, which is where a surviving run ends here");
        gcp.WorkerIdentityEnsureCalls.ShouldBe(0, "an adopted VM needs no identity");
        gcp.CreatedSpecs.Count.ShouldBe(1, "only the seeded VM exists; nothing was created");
    }

    [Fact]
    public async Task An_identity_step_longer_than_the_general_call_deadline_but_inside_its_own_budget_still_reaches_the_create()
    {
        // The real gateway is a dozen or more HTTP calls (account, role, two policy edits with retries); the general
        // CallTimeout would abandon it mid-write. The step has its own budget (CloudRunSettings.WorkerIdentityTimeout).
        // On test time (#567): the step "takes" 250 ms by moving the runner's clock inside the call, past the 50 ms general deadline (whose timer was armed before the call)
        // and well inside the identity budget, so a regression that used the general deadline here cancels the step's token and fails the run.
        var gcp = new FakeGcp();
        var clocks = new RunnerClocks();
        var slow = new SlowIdentity(gcp, TimeSpan.FromMilliseconds(250), clocks.Run);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, slow, repo)
        {
            CallTimeout = TimeSpan.FromMilliseconds(50),
            WorkerIdentityTimeout = TimeSpan.FromSeconds(10),
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            CreateTimeout = TimeSpan.FromSeconds(5),
            UploadTimeout = TimeSpan.FromSeconds(5),
            BootTimeout = TimeSpan.FromSeconds(5),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };

        var result = await runner.RunAsync(TestInputs.Request("job-i6", Spec("job-i6")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.ShouldNotBeEmpty("the slow identity step still reached the create");
    }

    private sealed class SlowIdentity(IWorkerIdentityGateway inner, TimeSpan delay, VirtualTimeProvider clock) : IWorkerIdentityGateway
    {
        public async Task<WorkerIdentity> EnsureWorkerIdentityAsync(CloudScope scope, string bucket, CancellationToken cancellationToken)
        {
            await Task.Yield();
            clock.Advance(delay);
            cancellationToken.ThrowIfCancellationRequested();
            return await inner.EnsureWorkerIdentityAsync(scope, bucket, cancellationToken);
        }
    }

    private sealed class RecordingIdentity(IWorkerIdentityGateway inner, Func<int> vmsCreated) : IWorkerIdentityGateway
    {
        public List<(string Project, string Bucket, int VmsCreatedSoFar)> Calls { get; } = [];

        public Task<WorkerIdentity> EnsureWorkerIdentityAsync(CloudScope scope, string bucket, CancellationToken cancellationToken)
        {
            Calls.Add((scope.ProjectId, bucket, vmsCreated()));
            return inner.EnsureWorkerIdentityAsync(scope, bucket, cancellationToken);
        }
    }
}
