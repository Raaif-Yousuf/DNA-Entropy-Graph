using System.Diagnostics;
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

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, TimeSpan ackTimeout, TimeSpan? finishTimeout = null) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        CancelAckTimeout = ackTimeout,
        CancelFinishTimeout = finishTimeout ?? TimeSpan.FromSeconds(5),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(200),
    };

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
        var clock = Stopwatch.StartNew();

        await Runner(gcp, repo, TimeSpan.FromSeconds(5)).CancelAsync("job-live", CancellationToken.None);

        gcp.GetObjectBytes(bucket, prefix + "control/cancel").ShouldBeEmpty("the cancel request is a 0-byte object (job_contract.md section 6)");
        Encoding.UTF8.GetString(gcp.GetObjectBytes(bucket, prefix + "status.json")).ShouldContain("\"stage\":\"cancelled\"", Case.Sensitive, "the worker saw the request and wrote its cancelled stage");
        Encoding.UTF8.GetString(gcp.GetObjectBytes(bucket, prefix + "result.json")).ShouldContain("cancelled");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "an answering worker does not cost the whole window");
        (await VmStatusAsync(gcp, "job-live")).ShouldBeNull();
        LastRow(repo, "job-live").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_worker_that_gives_no_sign_of_life_after_the_request_costs_the_ack_window_and_the_VM_is_then_deleted()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-dead");
        var clock = Stopwatch.StartNew();

        await Runner(gcp, repo, TimeSpan.FromMilliseconds(150)).CancelAsync("job-dead", CancellationToken.None);

        gcp.ObjectKeys(bucket).ShouldContain(prefix + "control/cancel");
        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140), "the dead worker was given its whole ack window");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await VmStatusAsync(gcp, "job-dead")).ShouldBeNull();
        LastRow(repo, "job-dead").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_worker_that_answers_but_never_finishes_is_given_the_finish_window_then_the_VM_is_deleted_anyway()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone).WithWorkerIgnoringCancel();
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-slow");
        var clock = Stopwatch.StartNew();

        await Runner(gcp, repo, TimeSpan.FromSeconds(5), finishTimeout: TimeSpan.FromMilliseconds(150)).CancelAsync("job-slow", CancellationToken.None);

        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
        (await VmStatusAsync(gcp, "job-slow")).ShouldBeNull();
        LastRow(repo, "job-slow").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_worker_that_never_started_is_not_waited_for_there_is_nobody_to_answer()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.NoHeartbeat);
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-boot");
        var clock = Stopwatch.StartNew();

        await Runner(gcp, repo, TimeSpan.FromSeconds(30)).CancelAsync("job-boot", CancellationToken.None);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "no worker status means no ack window");
        (await VmStatusAsync(gcp, "job-boot")).ShouldBeNull();
        LastRow(repo, "job-boot").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_row_that_does_not_know_its_bucket_yet_cancels_as_before_with_no_request_written()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-nobucket", bucketInRow: false);

        await Runner(gcp, repo, TimeSpan.FromSeconds(30)).CancelAsync("job-nobucket", CancellationToken.None);

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

        await Runner(gcp, repo, TimeSpan.FromSeconds(30)).CancelAsync("job-nowrite", CancellationToken.None);

        (await VmStatusAsync(gcp, "job-nowrite")).ShouldBeNull();
        LastRow(repo, "job-nowrite").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_caller_that_gives_up_during_the_wait_still_gets_the_VM_deleted_and_the_run_recorded()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning);
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-giveup");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var clock = Stopwatch.StartNew();

        await Runner(gcp, repo, TimeSpan.FromSeconds(30)).CancelAsync("job-giveup", cts.Token);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the 30 s window was cut by the caller");
        (await VmStatusAsync(gcp, "job-giveup")).ShouldBeNull("a VM that may be billing must not outlive a caller who gave up");
        LastRow(repo, "job-giveup").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_cancel_ack_of_zero_turns_the_graceful_step_off()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();
        var (bucket, prefix) = await SeedRunningAsync(gcp, repo, "job-off");

        await Runner(gcp, repo, TimeSpan.Zero).CancelAsync("job-off", CancellationToken.None);

        gcp.ObjectKeys(bucket).ShouldNotContain(prefix + "control/cancel");
        (await VmStatusAsync(gcp, "job-off")).ShouldBeNull();
    }
}
