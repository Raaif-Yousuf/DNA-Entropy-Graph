using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #675 (docs/job_contract.md section 6): a cancel first asks the worker to stop (writes <c>control/cancel</c>) and waits for a sign of life
/// within the cancel-ack window (60 s by default) so partial results are kept, and only then deletes the VM. A worker that never answers, or
/// answers and never finishes, costs the user only the window, never a VM left billing. Every test asserts the fake VM and the recorded row.
/// </summary>
public class CloudJobRunnerGracefulCancelTests
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

    /// <summary>
    /// The runner on VIRTUAL time (#525, #675 review): the ack and finish windows are measured on <paramref name="clock"/>, which moves only when the runner sleeps between looks,
    /// so a loaded machine cannot stretch a window and a test never waits real milliseconds. <paramref name="onSleep"/> sees each sleep (a test cancels the caller from it).
    /// </summary>
    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, VirtualTimeProvider clock, TimeSpan ackTimeout, TimeSpan? finishTimeout = null, Action? onSleep = null) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        CancelAckTimeout = ackTimeout,
        CancelFinishTimeout = finishTimeout ?? TimeSpan.FromSeconds(5),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(200),
        TimeProvider = clock,
        PollDelay = (span, token) =>
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(span);
            onSleep?.Invoke();
            return Task.CompletedTask;
        },
    };

    private static TimeSpan Elapsed(VirtualTimeProvider clock) => TimeSpan.FromTicks(clock.GetTimestamp());

    /// <summary>A run that is Running: its VM exists (the fake starts the simulated worker for it), its manifest is in the bucket, and its row says where.</summary>
    private static async Task<(string Bucket, string Prefix)> SeedRunningAsync(FakeGcp gcp, InMemoryRunRepository repo, string jobId, bool bucketInRow = true)
    {
        var bucket = await gcp.EnsureBucketAsync(Project, CancellationToken.None);
        var prefix = $"jobs/{jobId}/";
        gcp.PutObject(bucket, prefix + "manifest.json", Encoding.UTF8.GetBytes("{\"schema\":1}"));
        await gcp.CreateVmAsync(Spec(jobId), Zone, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord(jobId, JobPhase.Running, DateTimeOffset.UtcNow, Target: "cloud", VmName: $"deg-{jobId}", Bucket: bucketInRow ? bucket : null), CancellationToken.None);
        return (bucket, prefix);
    }

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId)
        => (await gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;

    [Fact]
    public async Task A_live_worker_is_asked_to_cancel_acknowledges_and_finishes_and_only_then_is_the_VM_deleted()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-live");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(5)).CancelAsync("job-live", CancellationToken.None);

        gcp.GetObjectBytes(bucket, prefix + "control/cancel").ShouldBeEmpty("the cancel request is a 0-byte object (job_contract.md section 6)");
        Encoding.UTF8.GetString(gcp.GetObjectBytes(bucket, prefix + "status.json")).ShouldContain("\"stage\":\"cancelled\"", Case.Sensitive, "the worker saw the request and wrote its cancelled stage");
        Encoding.UTF8.GetString(gcp.GetObjectBytes(bucket, prefix + "result.json")).ShouldContain("cancelled");
        Elapsed(clock).ShouldBeLessThan(TimeSpan.FromSeconds(3), "an answering worker does not cost the whole window");
        (await VmStatusAsync(gcp, "job-live")).ShouldBeNull();
        LastRow(repo, "job-live").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_worker_that_gives_no_sign_of_life_after_the_request_costs_the_ack_window_and_the_VM_is_then_deleted()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-dead");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromMilliseconds(150)).CancelAsync("job-dead", CancellationToken.None);

        gcp.ObjectKeys(bucket).ShouldContain(prefix + "control/cancel");
        Elapsed(clock).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), "the dead worker was given its whole ack window");
        Elapsed(clock).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await VmStatusAsync(gcp, "job-dead")).ShouldBeNull();
        LastRow(repo, "job-dead").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_worker_that_answers_but_never_finishes_is_given_the_finish_window_then_the_VM_is_deleted_anyway()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading).WithWorkerIgnoringCancel();
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-slow");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(5), finishTimeout: TimeSpan.FromMilliseconds(150)).CancelAsync("job-slow", CancellationToken.None);

        Elapsed(clock).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
        Elapsed(clock).ShouldBeLessThan(TimeSpan.FromSeconds(4));
        (await VmStatusAsync(gcp, "job-slow")).ShouldBeNull();
        LastRow(repo, "job-slow").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_worker_that_never_started_is_not_waited_for_there_is_nobody_to_answer()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.NoHeartbeat);
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-boot");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(30)).CancelAsync("job-boot", CancellationToken.None);

        Elapsed(clock).ShouldBeLessThan(TimeSpan.FromSeconds(3), "no worker status means no ack window");
        (await VmStatusAsync(gcp, "job-boot")).ShouldBeNull();
        LastRow(repo, "job-boot").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_row_that_does_not_know_its_bucket_yet_cancels_as_before_with_no_request_written()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-nobucket", bucketInRow: false);
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(30)).CancelAsync("job-nobucket", CancellationToken.None);

        gcp.ObjectKeys(bucket).ShouldNotContain(prefix + "control/cancel");
        (await VmStatusAsync(gcp, "job-nobucket")).ShouldBeNull();
        LastRow(repo, "job-nobucket").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_request_that_cannot_be_written_does_not_stop_the_cancel_the_VM_is_still_deleted()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone).WithUploadFailure("control/cancel", CloudErrorKind.Permission);
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-nowrite");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(30)).CancelAsync("job-nowrite", CancellationToken.None);

        (await VmStatusAsync(gcp, "job-nowrite")).ShouldBeNull();
        LastRow(repo, "job-nowrite").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_caller_that_gives_up_during_the_wait_still_gets_the_VM_deleted_and_the_run_recorded()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning);
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-giveup");
        using var cts = new CancellationTokenSource();
        var clock = new VirtualTimeProvider();
        var sleeps = 0;

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(30), onSleep: () => { if (++sleeps == 50) { cts.Cancel(); } }).CancelAsync("job-giveup", cts.Token);

        Elapsed(clock).ShouldBeLessThan(TimeSpan.FromSeconds(5), "the 30 s window was cut by the caller");
        (await VmStatusAsync(gcp, "job-giveup")).ShouldBeNull("a VM that may be billing must not outlive a caller who gave up");
        LastRow(repo, "job-giveup").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_cancel_ack_of_zero_turns_the_graceful_step_off()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-off");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.Zero).CancelAsync("job-off", CancellationToken.None);

        gcp.ObjectKeys(bucket).ShouldNotContain(prefix + "control/cancel");
        (await VmStatusAsync(gcp, "job-off")).ShouldBeNull();
    }

    // ---------------------------------------------------------------- #675 review

    [Fact]
    public async Task A_heartbeat_that_keeps_ticking_is_not_an_acknowledgement_the_worker_must_say_it_saw_the_request()
    {
        // A live worker (its heartbeat moves on every look) that never reports cancelSeen: before the explicit acknowledgement, the moving heartbeat alone ended the wait as "answered".
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading).WithWorkerNeverAcknowledgingCancel();
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-tick");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromMilliseconds(150), finishTimeout: TimeSpan.FromHours(1)).CancelAsync("job-tick", CancellationToken.None);

        gcp.ObjectKeys(bucket).ShouldContain(prefix + "control/cancel");
        Elapsed(clock).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), "no cancelSeen: the whole ack window passed");
        Elapsed(clock).ShouldBeLessThan(TimeSpan.FromMinutes(1), "and the finish window (an hour here) was never entered");
        (await VmStatusAsync(gcp, "job-tick")).ShouldBeNull();
        LastRow(repo, "job-tick").Phase.ShouldBe(JobPhase.Cancelled);
        LastRow(repo, "job-tick").JobPrefix.ShouldBeNull("nothing was acknowledged, so nothing is promised about partial outputs");
    }

    [Fact]
    public async Task An_acknowledged_cancel_keeps_the_row_pointing_at_the_job_folder_so_the_partial_outputs_can_be_downloaded_again()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-keep");
        gcp.PutObject(bucket, prefix + "output/in1/track.bedgraph", [1, 2, 3]); // what the worker uploaded before it stopped
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(5)).CancelAsync("job-keep", CancellationToken.None);

        var row = LastRow(repo, "job-keep");
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.Bucket.ShouldBe(bucket);
        row.JobPrefix.ShouldBe(prefix);
        gcp.ObjectKeys(bucket).ShouldContain(prefix + "output/in1/track.bedgraph", "the VM delete never touches the bucket");
    }

    [Fact]
    public async Task An_acknowledged_worker_that_is_slow_to_stop_still_costs_only_the_finish_window()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading).WithWorkerIgnoringCancel();
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-ack-slow");
        var clock = new VirtualTimeProvider();

        await Runner(gcp, repo, clock, TimeSpan.FromSeconds(5), finishTimeout: TimeSpan.FromMilliseconds(200)).CancelAsync("job-ack-slow", CancellationToken.None);

        Elapsed(clock).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        Elapsed(clock).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await VmStatusAsync(gcp, "job-ack-slow")).ShouldBeNull();
        LastRow(repo, "job-ack-slow").JobPrefix.ShouldBe("jobs/job-ack-slow/");
    }

    /// <summary>The fake behind the compute interface, except that the FIRST look by job id (the graceful step's) throws something that is not a cloud error.</summary>
    private sealed class FirstFindThrows(FakeGcp inner) : IComputeGateway
    {
        private int _finds;

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
            => Interlocked.Increment(ref _finds) == 1 ? throw new InvalidOperationException("an unexpected failure of the look") : inner.FindByJobIdAsync(scope, jobId, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(scope, spec, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(scope, spec, existingVmName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(scope, acceleratorType, cancellationToken);

        public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(scope, installationId, cancellationToken);

        public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(scope, diskName, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(scope, vmName, zone, cancellationToken);

        public Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(scope, vmName, zone, cancellationToken);

        public Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(scope, installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken) => inner.ListAppVmsAsync(scope, cancellationToken);
    }

    [Fact]
    public async Task Any_exception_from_the_graceful_ask_still_deletes_the_VM_and_leaves_the_row_terminal()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-throws");
        var runner = new CloudJobRunner(new FirstFindThrows(gcp), gcp, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(200),
        };

        await runner.CancelAsync("job-throws", CancellationToken.None);

        (await VmStatusAsync(gcp, "job-throws")).ShouldBeNull("a VM that may be billing is deleted whatever the ask threw");
        LastRow(repo, "job-throws").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_cancel_racing_a_driver_that_is_finishing_leaves_one_terminal_row_and_no_VM_billing()
    {
        for (var round = 0; round < 15; round++)
        {
            var jobId = $"job-race-{round}";
            var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
            var repo = new InMemoryRunRepository();
            var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
            {
                ResultPollInterval = TimeSpan.FromMilliseconds(1),
                CancelAckTimeout = TimeSpan.FromMilliseconds(50),
                LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
                LifecycleTimeout = TimeSpan.FromMilliseconds(200),
            };
            var active = new ActiveRuns();
            var driver = active.TryStart(jobId, token => runner.RunAsync(TestInputs.Request(jobId, Spec(jobId)), token)).ShouldNotBeNull();

            await Task.Delay(round % 5, CancellationToken.None); // a different moment of the run each round
            await active.CancelAsync(jobId, () => runner.CancelAsync(jobId, CancellationToken.None));
            await driver;

            // A cancel that wins before the driver recorded anything leaves no row and so nothing to end; any row that exists is terminal.
            if (repo.AllRecordedInOrder.LastOrDefault(r => r.JobId == jobId) is { } last)
            {
                JobStateMachine.IsTerminal(last.Phase).ShouldBeTrue($"round {round}: the row ended {last.Phase}");
            }
            var vm = await VmStatusAsync(gcp, jobId);
            (vm is null || vm == "STOPPED").ShouldBeTrue($"round {round}: the VM is {vm}, never left running");
        }
    }
}
