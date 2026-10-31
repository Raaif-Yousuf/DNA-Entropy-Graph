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

    // ---- M: the heartbeat write reads one job's row, honours the run's token, and only a heartbeat newer than the recorded one counts ----

    [Fact]
    public async Task Recording_a_heartbeat_reads_the_one_row_and_never_the_whole_run_table()
    {
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-m1", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        for (var i = 0; i < 50; i++)
        {
            await repo.UpsertAsync(new RunRecord($"other-{i}", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
        }

        var before = repo.GetAllCount;
        var at = DateTimeOffset.UtcNow;
        await new RunRowStore(repo, null).RecordHeartbeatAsync("job-m1", at, CancellationToken.None);

        repo.GetAllCount.ShouldBe(before, "a heartbeat write must not scan every run in the history");
        LastRow(repo, "job-m1").LastHeartbeatAt.ShouldBe(at);
    }

    [Fact]
    public async Task Recording_a_heartbeat_with_a_cancelled_token_writes_nothing_and_throws()
    {
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-m2", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        var written = repo.AllRecordedInOrder.Count;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => new RunRowStore(repo, null).RecordHeartbeatAsync("job-m2", DateTimeOffset.UtcNow, cts.Token));

        repo.AllRecordedInOrder.Count.ShouldBe(written, "a cancelled run writes no more heartbeats");
    }

    [Fact]
    public async Task A_heartbeat_not_newer_than_the_one_already_recorded_changes_nothing()
    {
        var recorded = new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-m3", JobPhase.Running, DateTimeOffset.UtcNow, LastHeartbeatAt: recorded), CancellationToken.None);
        var written = repo.AllRecordedInOrder.Count;
        var store = new RunRowStore(repo, null);

        await store.RecordHeartbeatAsync("job-m3", recorded.AddMinutes(-20), CancellationToken.None); // a stale status.json found on resume
        await store.RecordHeartbeatAsync("job-m3", recorded, CancellationToken.None);

        repo.AllRecordedInOrder.Count.ShouldBe(written, "an older or equal heartbeat is not written");
        LastRow(repo, "job-m3").LastHeartbeatAt.ShouldBe(recorded);

        await store.RecordHeartbeatAsync("job-m3", recorded.AddMinutes(5), CancellationToken.None);
        LastRow(repo, "job-m3").LastHeartbeatAt.ShouldBe(recorded.AddMinutes(5), "a newer one is");
    }

    [Fact]
    public async Task A_heartbeat_is_never_recorded_on_a_row_that_is_gone()
    {
        var repo = new InMemoryRunRepository();

        await new RunRowStore(repo, null).RecordHeartbeatAsync("no-such-job", DateTimeOffset.UtcNow, CancellationToken.None);

        repo.AllRecordedInOrder.ShouldBeEmpty();
    }

    [Fact]
    public void The_watch_reports_the_workers_own_update_time_of_a_new_heartbeat_and_none_for_one_it_already_saw()
    {
        var watch = new HeartbeatWatch(expectsGpu: false, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var status = """{"schema":1,"jobId":"j","stage":"running","worker":{"version":"0.1.0"},"heartbeatSeq":3,"updatedAt":"2026-10-01T12:20:00Z"}""";

        watch.Observe(status, null, TimeSpan.FromSeconds(1));
        watch.NewHeartbeatAt.ShouldBe(new DateTimeOffset(2026, 10, 1, 12, 20, 0, TimeSpan.Zero), "the heartbeat's own time, not the app's clock");

        watch.Observe(status, null, TimeSpan.FromSeconds(2));
        watch.NewHeartbeatAt.ShouldBeNull("the same status again is not a new heartbeat");
    }

    [Fact]
    public async Task A_run_records_the_workers_heartbeat_time_through_the_runs_own_token_and_one_row_read()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone).WithWorkerClock(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = new InMemoryRunRepository();
        var clocks = new RunnerClocks();
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
        using var cts = new CancellationTokenSource();

        var result = await runner.RunAsync(TestInputs.Request("job-m6", Spec("job-m6")), cts.Token);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        repo.GetTokens.ShouldContain(cts.Token, "the heartbeat write was handed the run's own cancellation token");
        // The fake worker's updatedAt is pinned to 2099, far ahead of the runner's (test) clock: the row holds the earlier, the app's own now.
        LastRow(repo, "job-m6").LastHeartbeatAt!.Value.ShouldBeLessThanOrEqualTo(clocks.Run.GetUtcNow());
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

    // ---- round 10: clock skew, a missing updatedAt, negative spans, the token ----

    private static readonly DateTimeOffset AppNow = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static RunRecord Row(string jobId) => new(jobId, JobPhase.Running, AppNow, Target: "cloud");

    [Fact]
    public async Task A_heartbeat_from_a_VM_clock_ahead_of_the_app_is_recorded_as_the_apps_now()
    {
        var clocks = new RunnerClocks();
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone).WithWorkerClock(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
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

        var result = await runner.RunAsync(TestInputs.Request("job-r10a", Spec("job-r10a")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        // The fake worker's clock is pinned to 2099, far ahead of the run's test clock: the row must never hold a time later than the app's own now.
        var recorded = repo.AllRecordedInOrder.Where(r => r.JobId == "job-r10a" && r.LastHeartbeatAt is not null).Select(r => r.LastHeartbeatAt!.Value).ToList();
        recorded.ShouldNotBeEmpty();
        recorded.ShouldAllBe(at => at <= clocks.Run.GetUtcNow(), "a future heartbeat rejects every later real one and is booked as the end");
    }

    [Fact]
    public async Task A_later_heartbeat_records_after_an_earlier_one()
    {
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(Row("job-r10b"), CancellationToken.None);
        var store = new RunRowStore(repo, null);

        await store.RecordHeartbeatAsync("job-r10b", AppNow, CancellationToken.None);
        await store.RecordHeartbeatAsync("job-r10b", AppNow.AddMinutes(1), CancellationToken.None);

        (await repo.GetAsync("job-r10b", CancellationToken.None))!.LastHeartbeatAt.ShouldBe(AppNow.AddMinutes(1));
    }

    [Fact]
    public async Task The_heartbeat_write_is_one_keyed_read_and_that_read_carries_the_token()
    {
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(Row("job-r10c"), CancellationToken.None);
        using var cts = new CancellationTokenSource();

        await new RunRowStore(repo, null).RecordHeartbeatAsync("job-r10c", AppNow, cts.Token);

        repo.GetTokens.Count.ShouldBe(1, "exactly one keyed read");
        repo.GetTokens.Single().ShouldBe(cts.Token, "and that read is the call that carried the token");
    }

    [Fact]
    public void A_new_heartbeat_with_no_readable_update_time_after_the_first_look_records_the_apps_clock()
    {
        var watch = new HeartbeatWatch(expectsGpu: false, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        static string Status(int seq) => $$"""{"schema":1,"jobId":"j","stage":"running","worker":{"version":"0.1.0"},"heartbeatSeq":{{seq}}}""";

        watch.Observe(Status(3), null, TimeSpan.FromSeconds(1), AppNow);
        watch.NewHeartbeatAt.ShouldBeNull("the first look at a (possibly resumed) status.json is not proof of life now");

        watch.Observe(Status(4), null, TimeSpan.FromSeconds(2), AppNow.AddSeconds(1));
        watch.NewHeartbeatAt.ShouldBe(AppNow.AddSeconds(1), "the sequence advanced while this watch was looking");

        watch.Observe(Status(4), null, TimeSpan.FromSeconds(3), AppNow.AddSeconds(2));
        watch.NewHeartbeatAt.ShouldBeNull("no change, no heartbeat");
    }

    [Fact]
    public void A_gone_VM_never_ends_after_now_even_when_a_sign_of_life_is_in_the_future()
    {
        var future = AppNow.AddHours(1);

        VmSpan.EndOfGone(AppNow.AddMinutes(-10), TimeSpan.FromHours(4), AppNow, null, future).ShouldBe(AppNow);
        VmSpan.EndOfGone(AppNow.AddMinutes(-10), TimeSpan.FromHours(4), AppNow, Row("j") with { LastHeartbeatAt = future }).ShouldBe(AppNow);
    }

    [Fact]
    public void A_gone_VM_whose_start_is_after_now_has_a_zero_span_not_a_negative_one()
    {
        var start = AppNow.AddMinutes(5);

        VmSpan.EndOfGone(start, TimeSpan.FromHours(4), AppNow, null).ShouldBe(AppNow);
        VmSpan.EndOfGone(start, TimeSpan.FromHours(4), AppNow, null, AppNow.AddMinutes(1)).ShouldBe(AppNow);
    }
}
