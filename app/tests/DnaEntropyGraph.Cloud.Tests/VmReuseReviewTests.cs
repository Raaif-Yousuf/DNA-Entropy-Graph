using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Cold-review findings on the reuse of a stopped VM (#94), each written from the symptom: a reused VM that ends at once as a
/// timeout, a cancel that leaves a billing VM, a lost answer that deletes the VM that just started, two jobs claiming one VM.
/// </summary>
public class VmReuseReviewTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId, TimeSpan? maxRun = null) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: maxRun ?? TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static async Task SeedIdleVmAsync(FakeGcp gcp, InMemoryRunRepository repo, string oldJob)
    {
        var spec = Spec(oldJob);
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        await gcp.StopVmAsync(spec.VmName, Zone, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord(oldJob, JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private sealed class HookedCompute(FakeGcp inner) : IComputeGateway
    {
        public Func<VmSpec, string, string, CancellationToken, Task<VmDescriptor>>? OnReuse { get; set; }

        public Func<string, string, CancellationToken, Task<VmDescriptor?>>? OnGet { get; set; }

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
            => OnReuse is null ? inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken) : OnReuse(spec, existingVmName, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
            => OnGet is null ? inner.GetVmAsync(vmName, zone, cancellationToken) : OnGet(vmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);
    }

    private static CloudJobRunner Runner(IComputeGateway compute, FakeGcp gcp, InMemoryRunRepository repo, TimeProvider? time = null, TimeSpan? resultTimeout = null) => new(compute, gcp, gcp, gcp, gcp, repo)
    {
        TimeProvider = time ?? TimeProvider.System,
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = resultTimeout,
        CallTimeout = TimeSpan.FromSeconds(10),
        CreateTimeout = TimeSpan.FromSeconds(10),
        CreateSettleTimeout = TimeSpan.FromSeconds(5),
        UploadTimeout = TimeSpan.FromSeconds(10),
        BootTimeout = TimeSpan.FromSeconds(10),
        LifecycleTimeout = TimeSpan.FromSeconds(1),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
    };

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-02T08:00:00Z");

    // ---- 1: a reused VM keeps its creationTimestamp; the platform's limit counts from the last start ----

    [Fact]
    public async Task A_VM_created_days_ago_and_reused_now_is_not_ended_at_once_as_a_result_timeout()
    {
        var time = new ManualTimeProvider(Start);
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        time.Advance(TimeSpan.FromDays(3));

        var request = TestInputs.Request("job-new", Spec("job-new"));
        var looks = 0;
        var compute = new HookedCompute(gcp)
        {
            OnGet = (name, zone, token) =>
            {
                time.Advance(TimeSpan.FromMinutes(1));
                if (++looks == 4)
                {
                    gcp.SimulateWorkerFinishing(request.Spec with { ExistingVmName = "deg-job-old" }, Zone);
                }

                return gcp.GetVmAsync(name, zone, token);
            },
        };

        var result = await Runner(compute, gcp, repo, time).RunAsync(request, CancellationToken.None);

        gcp.ReuseAttempts.ShouldBe(1);
        result.FailureCode.ShouldBeNull(result.FailureMessage);
        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public async Task A_reused_VM_that_vanishes_soon_after_its_start_is_lost_not_past_its_run_limit()
    {
        var time = new ManualTimeProvider(Start);
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        time.Advance(TimeSpan.FromDays(3));

        var looks = 0;
        var compute = new HookedCompute(gcp)
        {
            OnGet = async (name, zone, token) =>
            {
                if (++looks == 3)
                {
                    await gcp.DeleteVmAsync(name, zone, token);
                }

                return await gcp.GetVmAsync(name, zone, token);
            },
        };

        var result = await Runner(compute, gcp, repo, time).RunAsync(TestInputs.Request("job-new", Spec("job-new", TimeSpan.FromHours(1))), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.VmUnhealthy, "the VM was reused minutes ago: its limit is not the three days since it was created");
    }

    [Fact]
    public async Task The_fake_keeps_creation_and_moves_the_start_like_the_real_instance_resource()
    {
        var time = new ManualTimeProvider(Start);
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        time.Advance(TimeSpan.FromDays(3));

        var vm = await gcp.ReuseVmAsync(Spec("job-new"), "deg-job-old", Zone, CancellationToken.None);

        vm.CreatedAt.ShouldBe(Start, "creationTimestamp never changes");
        vm.StartedAt.ShouldBe(Start + TimeSpan.FromDays(3), "lastStartTimestamp is the reuse");
        vm.RunClockStart.ShouldBe(vm.StartedAt);
    }

    // ---- 2: a cancel between the claim steps must see the reuse ----

    [Fact]
    public async Task A_cancel_during_the_reuse_leaves_no_VM_of_the_cancelled_job()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compute = new HookedCompute(gcp)
        {
            OnReuse = async (spec, name, zone, token) =>
            {
                entered.TrySetResult();
                await gate.Task;
                return await gcp.ReuseVmAsync(spec, name, zone, token);
            },
        };
        var runner = Runner(compute, gcp, repo, resultTimeout: TimeSpan.FromMilliseconds(300));

        var run = runner.RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var cancel = runner.CancelAsync("job-new", CancellationToken.None);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        gate.SetResult();
        await cancel.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var owned = await gcp.FindByJobIdAsync("job-new", CancellationToken.None);
        owned.ShouldBeEmpty("the reuse landed after the cancel looked: a VM of the cancelled job was left (billing, or a disk)");
        var last = repo.AllRecordedInOrder.Last(r => r.JobId == "job-new");
        // Which terminal state wins the race between the cancel and the run's own next step is a separate matter (the run's illegal
        // Cancelling -> Preparing step records Failed first); what this test owns is that nothing is left billing and the row ended.
        last.Phase.ShouldBeOneOf(JobPhase.Cancelled, JobPhase.Failed);
    }

    // ---- 5: the answer of a successful start is lost ----

    private static CloudCallPipeline FastPipeline(FakeGcp gcp) => new(new CloudRetryOptions { MaxRetryAttempts = 4, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero }, gcp, new CloudRetryLog());

    [Fact]
    public async Task A_lost_answer_after_the_start_is_not_replayed_as_a_whole()
    {
        var gcp = new FakeGcp().WithReuseResponseLost();
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        var compute = new ResilientComputeGateway(gcp, FastPipeline(gcp));

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => compute.ReuseVmAsync(Spec("job-new"), "deg-job-old", Zone, CancellationToken.None));

        thrown.Kind.ShouldBe(CloudErrorKind.Network);
        gcp.ReuseAttempts.ShouldBe(1, "replaying the composite meets a RUNNING VM and answers VM_NOT_STOPPED");
        (await gcp.GetVmAsync("deg-job-old", Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
    }

    [Fact]
    public async Task A_run_whose_reuse_start_succeeded_but_whose_answer_was_lost_adopts_that_VM_and_creates_nothing()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Done).WithReuseResponseLost();
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        var compute = new ResilientComputeGateway(gcp, FastPipeline(gcp));

        var result = await Runner(compute, gcp, repo, resultTimeout: TimeSpan.FromSeconds(20)).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.ReuseAttempts.ShouldBe(1);
        gcp.CreateAttempts.ShouldBe(1, "only the seed: the VM that had just started was deleted and a second one created");
        (await gcp.ListAppVmsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("deg-job-old");
    }

    // ---- 6: two jobs racing for one stopped VM ----

    [Fact]
    public async Task Of_two_jobs_claiming_one_stopped_VM_exactly_one_wins_and_the_VM_keeps_the_winners_limits()
    {
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never).WithReuseBarrier(async _ =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.TrySetResult();
            }

            await both.Task;
        });
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        var specA = Spec("job-a", TimeSpan.FromHours(2));
        var specB = Spec("job-b", TimeSpan.FromHours(3));

        var a = Settle(gcp.ReuseVmAsync(specA, "deg-job-old", Zone, CancellationToken.None));
        var b = Settle(gcp.ReuseVmAsync(specB, "deg-job-old", Zone, CancellationToken.None));
        var outcomes = await Task.WhenAll(a, b);

        outcomes.Count(o => o is null).ShouldBe(1, "exactly one claim may succeed");
        var loser = outcomes.Single(o => o is not null)!;
        loser.Error.HttpStatus.ShouldBe(412);
        var vm = (await gcp.GetVmAsync("deg-job-old", Zone, CancellationToken.None))!;
        var winner = outcomes[0] is null ? specA : specB;
        vm.Labels!["job-id"].ShouldBe(winner.JobId);
        gcp.MaxRunDurationOf("deg-job-old", Zone).ShouldBe(winner.MaxRunDuration, "a loser must not have overwritten the winner's maxRunDuration");
    }

    private static async Task<CloudOperationException?> Settle(Task<VmDescriptor> task)
    {
        try
        {
            await task;
            return null;
        }
        catch (CloudOperationException ex)
        {
            return ex;
        }
    }
}
