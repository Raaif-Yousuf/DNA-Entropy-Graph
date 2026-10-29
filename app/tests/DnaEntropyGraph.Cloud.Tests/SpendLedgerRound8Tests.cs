using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #109 round 8: what the spend ledger's span rule (VmSpan) does with an adopted VM, a cancel that races a create, a heartbeat and a gone VM with no evidence.</summary>
public class SpendLedgerRound8Tests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

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

    /// <summary>A compute gateway whose create, delete and label lookup a test can observe or override; everything else goes to the fake.</summary>
    private sealed class Spy(FakeGcp inner) : IComputeGateway
    {
        public Func<VmSpec, string, CancellationToken, Task<VmDescriptor>>? OnCreate { get; set; }

        public bool HideFromLabelLookup { get; set; }

        public int FindCalls;

        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
            => OnCreate is null ? inner.CreateVmAsync(spec, zone, cancellationToken) : OnCreate(spec, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public async Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            await inner.DeleteVmAsync(vmName, zone, cancellationToken);
            lock (Deleted)
            {
                Deleted.Add(vmName);
            }
        }

        public async Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FindCalls);
            return HideFromLabelLookup ? [] : await inner.FindByJobIdAsync(jobId, cancellationToken);
        }

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);
    }

    private static CloudJobRunner Runner(IComputeGateway compute, FakeGcp gcp, InMemoryRunRepository repo, TimeSpan? resultTimeout = null)
        => new(compute, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = resultTimeout ?? TimeSpan.FromSeconds(5),
            CallTimeout = TimeSpan.FromSeconds(1),
            EnsureBucketTimeout = TimeSpan.FromSeconds(1),
            CreateTimeout = TimeSpan.FromSeconds(1),
            CreateSettleTimeout = TimeSpan.FromMilliseconds(80),
            UploadTimeout = TimeSpan.FromSeconds(2),
            BootTimeout = TimeSpan.FromSeconds(2),
            LifecycleTimeout = TimeSpan.FromSeconds(2),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    private static async Task<(Task Run, CancellationTokenSource Cts)> StartBlockedRunAsync(FakeGcp gcp, CloudJobRunner runner, string jobId)
    {
        var cts = new CancellationTokenSource();
        var run = runner.RunAsync(TestInputs.Request(jobId, Spec(jobId)), cts.Token);
        await gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);
        return (run, cts);
    }

    // ---- M: a create answered AlreadyExists adopts the VM and books its span ----

    [Fact]
    public async Task A_create_answered_AlreadyExists_adopts_the_VM_and_books_from_its_own_start()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow.AddHours(-3));
        var gcp = new FakeGcp(time);
        var request = TestInputs.Request("job-x1", Spec("job-x1"));
        var existing = await gcp.CreateVmAsync(request.Spec, Zone, CancellationToken.None);
        var spy = new Spy(gcp)
        {
            // The label listing does not show the VM yet (it lags), so the run does not adopt it up front and its own create answers AlreadyExists.
            HideFromLabelLookup = true,
            OnCreate = (_, _, _) => throw new CloudOperationException(new CloudError("ALREADY_EXISTS", 409, "already exists"), CloudErrorKind.AlreadyExists),
        };
        var repo = new InMemoryRunRepository();

        // The VM was made outside this run, so no simulated worker answers: the run ends at its result limit. What matters is the span the adoption recorded.
        await Runner(spy, gcp, repo, TimeSpan.FromMilliseconds(100)).RunAsync(request, CancellationToken.None);

        LastRow(repo, "job-x1").VmReadyAt.ShouldBe(existing.RunClockStart, "the adopted VM billed from its own start; without it the ledger books nothing");
    }

    // ---- M: a cancel racing an in-flight create ----

    [Fact]
    public async Task A_create_that_lands_after_the_cancel_gave_up_waiting_is_deleted_by_name_when_it_lands()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var spy = new Spy(gcp);
        var repo = new InMemoryRunRepository();
        var runner = Runner(spy, gcp, repo);
        var (_, cts) = await StartBlockedRunAsync(gcp, runner, "job-x2");
        using var _ = cts;

        await runner.CancelAsync("job-x2", CancellationToken.None);
        LastRow(repo, "job-x2").Phase.ShouldBe(JobPhase.Failed, "the create was still in flight when the cancel stopped waiting, so the VM end is not confirmed");

        gcp.ReleaseCreate();
        var waited = 0;
        while (spy.Deleted.Count == 0 && waited++ < 500)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        spy.Deleted.ShouldContain("deg-job-x2", "the late landing of a cancelled job is deleted by its own name, not left billing until maxRunDuration");
        (await gcp.GetVmAsync("deg-job-x2", Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_cancel_deletes_and_books_the_VM_a_create_answered_even_when_the_label_lookup_does_not_list_it_yet()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var spy = new Spy(gcp) { HideFromLabelLookup = true };
        var repo = new InMemoryRunRepository();
        var runner = Runner(spy, gcp, repo);
        var (_, cts) = await StartBlockedRunAsync(gcp, runner, "job-x3");
        using var _ = cts;

        var cancel = runner.CancelAsync("job-x3", CancellationToken.None);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        gcp.ReleaseCreate();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        (await gcp.GetVmAsync("deg-job-x3", Zone, CancellationToken.None)).ShouldBeNull("the create's own answer named the VM; a label listing that lags must not leave it running");
        var row = LastRow(repo, "job-x3");
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.VmReadyAt.ShouldNotBeNull("the VM billed from its start until the cancel deleted it");
    }

    [Fact]
    public async Task A_cancel_that_finds_no_VM_looked_and_books_no_span()
    {
        var gcp = new FakeGcp();
        var spy = new Spy(gcp);
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-x4", JobPhase.Cancelling, DateTimeOffset.UtcNow), CancellationToken.None);

        await Runner(spy, gcp, repo).CancelAsync("job-x4", CancellationToken.None);

        spy.FindCalls.ShouldBeGreaterThan(0, "the canceller must have looked for the job's VMs; a cancel that never looked proves nothing about booking");
        LastRow(repo, "job-x4").VmReadyAt.ShouldBeNull("the app never saw a VM for this job");
    }

    // ---- L: a cancel that finds several VMs books the earliest start and the latest end ----

    [Fact]
    public void The_span_across_several_VMs_is_the_earliest_start_and_the_latest_end_and_open_if_any_still_runs()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var early = new VmDescriptor("a", Zone, "TERMINATED", CreatedAt: t0, StoppedAt: t0.AddMinutes(10));
        var late = new VmDescriptor("b", Zone, "TERMINATED", CreatedAt: t0.AddMinutes(5), StoppedAt: t0.AddMinutes(30));
        var running = new VmDescriptor("c", Zone, "RUNNING", CreatedAt: t0.AddMinutes(7));

        VmSpan.Across([late, early]).ShouldBe((t0, t0.AddMinutes(30)));
        VmSpan.Across([late, running, early]).ShouldBe((t0, (DateTimeOffset?)null));
        VmSpan.Across([]).ShouldBe((null, null));
    }

    // ---- L: VmEndedAt is first-wins for the run ----

    [Fact]
    public void A_restarted_adopted_VM_keeps_the_stop_first_observed_for_the_run()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var progress = new RunProgress();

        progress.Observe(new VmDescriptor("a", Zone, "TERMINATED", CreatedAt: t0, StoppedAt: t0.AddMinutes(10)));
        progress.Observe(new VmDescriptor("a", Zone, "RUNNING", CreatedAt: t0, StartedAt: t0.AddMinutes(20)));

        progress.VmCreatedAt.ShouldBe(t0);
        progress.VmEndedAt.ShouldBe(t0.AddMinutes(10), "documented in VmSpan: the first observation of the VM wins for the run");
    }

    // ---- M: the cloud runner records the worker's fresh heartbeat on the row, at most once a minute ----

    [Fact]
    public async Task A_run_whose_worker_heartbeats_records_the_last_heartbeat_on_the_row_and_not_on_every_poll()
    {
        var clocks = new RunnerClocks();
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(20),
            FirstHeartbeatTimeout = TimeSpan.FromSeconds(30),
            HeartbeatStaleTimeout = TimeSpan.FromSeconds(30),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(200),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };

        var result = await runner.RunAsync(TestInputs.Request("job-x5", Spec("job-x5")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        LastRow(repo, "job-x5").LastHeartbeatAt.ShouldNotBeNull("without it a VM that deleted itself after the app closed is booked until the app came back");
        repo.AllRecordedInOrder.Where(r => r.JobId == "job-x5" && r.LastHeartbeatAt is not null).Select(r => r.LastHeartbeatAt).Distinct().Count()
            .ShouldBe(1, "the run is far shorter than a minute: one write, not one per poll");
    }
}
