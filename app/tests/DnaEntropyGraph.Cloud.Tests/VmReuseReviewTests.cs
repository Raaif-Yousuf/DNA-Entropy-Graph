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

        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
            => OnReuse is null ? inner.ReuseVmAsync(scope, spec, existingVmName, zone, cancellationToken) : OnReuse(spec, existingVmName, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
            => OnGet is null ? inner.GetVmAsync(scope, vmName, zone, cancellationToken) : OnGet(vmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(scope, spec, zone, cancellationToken);

        public Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(scope, vmName, zone, cancellationToken);

        public Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(scope, jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(scope, installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken) => inner.ListAppVmsAsync(scope, cancellationToken);

        public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(scope, installationId, cancellationToken);

        public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(scope, diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(scope, acceleratorType, cancellationToken);
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

    [Fact]
    public async Task If_the_platform_limit_counts_from_creation_a_reused_old_VM_ends_the_run_in_a_recorded_state_with_nothing_left()
    {
        // THEORY (unverified, ToTest #94): the limit restarts at each start. If it does not, a VM created days ago is deleted at once.
        var time = new ManualTimeProvider(Start);
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never).WithMaxRunDurationEnforced().WithRunClockFromCreation();
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        time.Advance(TimeSpan.FromDays(3));

        var result = await Runner(gcp, gcp, repo, time).RunAsync(TestInputs.Request("job-new", Spec("job-new", TimeSpan.FromHours(4))), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldNotBeNull();
        (await gcp.FindByJobIdAsync("job-new", CancellationToken.None)).ShouldBeEmpty();
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-new").Phase.ShouldBe(JobPhase.Failed);
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
        var runner = Runner(compute, gcp, repo, new VirtualTimeProvider(), resultTimeout: TimeSpan.FromMilliseconds(300));

        var run = runner.RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var cancel = runner.CancelAsync("job-new", CancellationToken.None);
        // Real time on purpose (#567): a window for the cancel to look while the reuse is held. Nothing observable says "the cancel has looked" on correct code (it waits for the reuse), so
        // this is a negative-evidence wait: too short under load can only hide the old bug (a false pass), it can never fail correct code.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        gate.SetResult();
        await cancel.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // #938 round 3 H1 (replaces "owned.ShouldBeEmpty"): the reused VM is another run's keep-alive VM that the reuse only relabelled. A cancel STOPS it (it must not bill) and never deletes it.
        var owned = await gcp.FindByJobIdAsync("job-new", CancellationToken.None);
        owned.ShouldAllBe(v => VmFacts.IsStopped(v), "the reuse landed after the cancel looked: a VM of the cancelled job was left running (billing)");
        gcp.DeleteAttempts.ShouldBeEmpty("the cancel deleted a VM another run created");
        var last = repo.AllRecordedInOrder.Last(r => r.JobId == "job-new");
        last.Phase.ShouldBe(JobPhase.Cancelled, "the cancel won the race with the run's next phase step (#687): Cancelled, not an illegal-transition Failed");
    }

    [Fact]
    public async Task A_cancel_that_arrives_before_the_reuse_call_returns_still_waits_for_it_and_ends_the_VM()
    {
        // MEASURED 2026-10-16 (#784): the in-flight reuse was tracked only AFTER ReuseVmAsync returned its task, so a cancel that looked
        // in between settled nothing, deleted nothing, and the reuse then landed a RUNNING VM under a Cancelled record. Deterministic here:
        // the gateway's synchronous part holds the run's thread until the cancel has recorded Cancelling and had time to look.
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        CloudJobRunner? runner = null;
        Task? cancel = null;
        var compute = new HookedCompute(gcp)
        {
            OnReuse = (spec, name, zone, token) =>
            {
                cancel = Task.Run(() => runner!.CancelAsync("job-new", CancellationToken.None));
                // Blocks this thread on the repository's own signal (the cancel recorded Cancelling), never on a poll.
                repo.WhenRecordedAsync(r => r.JobId == "job-new" && r.Phase == JobPhase.Cancelling).GetAwaiter().GetResult();
                // Real time on purpose (#567): a negative-evidence window for the cancel to look (and, unfixed, find nothing) before the reuse call returns. Too short under load can only hide
                // the old bug (a false pass); it can never fail the fixed code.
                Thread.Sleep(250);
                return gcp.ReuseVmAsync(spec, name, zone, token);
            },
        };
        runner = Runner(compute, gcp, repo, new VirtualTimeProvider(), resultTimeout: TimeSpan.FromMilliseconds(300));

        var run = runner.RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await cancel!.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // #938 round 3 H1 (replaces "ShouldBeEmpty"): the reused VM is another run's; a cancel stops it and never deletes it.
        (await gcp.FindByJobIdAsync("job-new", CancellationToken.None)).ShouldAllBe(v => VmFacts.IsStopped(v), "the cancel looked before the reuse was tracked: a VM of the cancelled job was left billing");
        gcp.DeleteAttempts.ShouldBeEmpty("the cancel deleted a VM another run created");
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-new").Phase.ShouldBe(JobPhase.Cancelled);
    }

    // ---- review: the run clock never compares the app's wall clock with the platform's ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_laptop_clock_an_hour_fast_does_not_fire_the_result_deadline_early(bool reused)
    {
        var app = new VirtualTimeProvider();
        var platform = new ManualTimeProvider(app.GetUtcNow() - TimeSpan.FromHours(1)); // the app's clock reads an hour ahead of the platform's
        var gcp = new FakeGcp(platform).WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        if (reused)
        {
            await SeedIdleVmAsync(gcp, repo, "job-old");
            platform.Advance(TimeSpan.FromDays(3));
            app.Advance(TimeSpan.FromDays(3));
        }

        var request = TestInputs.Request("job-new", Spec("job-new", TimeSpan.FromHours(4)));
        var polls = 0;
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
        {
            TimeProvider = app,
            ResultTimeout = null,
            FirstHeartbeatTimeout = TimeSpan.FromDays(2), // this worker is silent on purpose: only the result deadline is under test
            HeartbeatStaleTimeout = TimeSpan.FromDays(2),
            CallTimeout = TimeSpan.FromSeconds(10),
            CreateTimeout = TimeSpan.FromSeconds(10),
            BootTimeout = TimeSpan.FromSeconds(10),
            LifecycleTimeout = TimeSpan.FromSeconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            PollDelay = (_, _) =>
            {
                // One minute passes on BOTH clocks per poll; the worker finishes 3 h 20 min in, inside the 3 h 57 min limit.
                platform.Advance(TimeSpan.FromMinutes(1));
                app.Advance(TimeSpan.FromMinutes(1));
                if (++polls == 200)
                {
                    gcp.SimulateWorkerFinishing(reused ? request.Spec with { ExistingVmName = "deg-job-old" } : request.Spec, Zone);
                }

                return Task.CompletedTask;
            },
        };

        var result = await runner.RunAsync(request, CancellationToken.None);

        polls.ShouldBeGreaterThanOrEqualTo(200, "the run ended before the worker did: the deadline fired early");
        result.FailureCode.ShouldBeNull(result.FailureMessage);
        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }
    [Fact]
    public async Task A_claim_on_a_VM_that_someone_relabelled_in_between_loses_like_a_real_setLabels()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        gcp.WithReuseBarrier(_ =>
        {
            gcp.SimulateExternalRelabel("deg-job-old", Zone, new Dictionary<string, string> { ["note"] = "edited in the console" });
            return Task.CompletedTask;
        });

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => gcp.ReuseVmAsync(Spec("job-new"), "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.HttpStatus.ShouldBe(412, "any labels write moves the real labelFingerprint, not only a reuse");
    }

    [Fact]
    public async Task A_claim_read_before_the_VM_was_deleted_and_recreated_does_not_claim_the_new_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        // One claim moves the fingerprint of the first VM; then it is deleted and a new VM is made under the same name.
        await gcp.ReuseVmAsync(Spec("job-first"), "deg-job-old", Zone, CancellationToken.None);
        await gcp.StopVmAsync("deg-job-old", Zone, CancellationToken.None);
        var racer = Spec("job-racer");
        gcp.WithReuseBarrier(async _ =>
        {
            await gcp.DeleteVmAsync("deg-job-old", Zone, CancellationToken.None);
            await gcp.CreateVmAsync(Spec("job-old"), Zone, CancellationToken.None);
            await gcp.StopVmAsync("deg-job-old", Zone, CancellationToken.None);
        });

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => gcp.ReuseVmAsync(racer, "deg-job-old", Zone, CancellationToken.None));

        thrown.Error.HttpStatus.ShouldBe(412, "a recreated VM has a new labelFingerprint; the racer read the old one");
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
