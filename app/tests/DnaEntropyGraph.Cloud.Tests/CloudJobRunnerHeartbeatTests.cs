using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #498: RUNNING is not working. The runner reads the worker's heartbeat in <c>status.json</c> while it waits, so a
/// worker that wedges on a RUNNING VM ends the run (and the VM) long before the result wait limit, and a first progress
/// line that reports no GPU on a GPU tier fails the run as <c>gpu_not_visible</c>. Each test asserts the recorded state AND
/// the fake VM, because a run that failed but left the VM billing is the bug.
/// </summary>
public class CloudJobRunnerHeartbeatTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId, string machine = "g2-standard-8") => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: machine,
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo) => new(gcp, gcp, gcp, gcp, repo)
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = TimeSpan.FromSeconds(20),
        FirstHeartbeatTimeout = TimeSpan.FromMilliseconds(60),
        HeartbeatStaleTimeout = TimeSpan.FromMilliseconds(60),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(200),
    };

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId)
        => (await gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;

    [Fact]
    public async Task A_heartbeat_that_stops_after_Running_fails_the_run_as_stale_long_before_the_result_limit_and_the_VM_is_ended()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-stale", Spec("job-stale")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerHeartbeatStale, "the 20 s result limit would have recorded result_timeout instead");
        LastRow(repo, "job-stale").ErrorCode.ShouldBe(RunErrorCodes.WorkerHeartbeatStale);
        (await VmStatusAsync(gcp, "job-stale")).ShouldBe("STOPPED", "a wedged worker's VM must not keep billing (Hard Rule 11)");
    }

    [Fact]
    public async Task No_heartbeat_within_the_first_heartbeat_deadline_fails_the_run_and_ends_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.NoHeartbeat);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-nohb", Spec("job-nohb")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerNoHeartbeat);
        (await VmStatusAsync(gcp, "job-nohb")).ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_first_progress_line_with_no_GPU_on_a_GPU_tier_fails_as_gpu_not_visible_and_deletes_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.NoGpuFirstLine);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-nogpu", Spec("job-nogpu")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.GpuNotVisible);
        (await VmStatusAsync(gcp, "job-nogpu")).ShouldBeNull("a box with no GPU is deleted, as startup.sh does");
    }

    [Fact]
    public async Task No_GPU_on_a_CPU_machine_is_not_a_failure()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.NoGpuFirstLine);
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);

        var result = await runner.RunAsync(TestInputs.Request("job-cpu", Spec("job-cpu", "e2-standard-4")), CancellationToken.None);

        // The CPU worker reports no GPU and then never finishes in this mode: the heartbeat goes stale, not gpu_not_visible.
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerHeartbeatStale);
    }

    [Fact]
    public async Task A_worker_whose_heartbeat_keeps_advancing_is_not_failed_and_finishes()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-alive", Spec("job-alive")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        result.FailureCode.ShouldBeNull();
    }
}
