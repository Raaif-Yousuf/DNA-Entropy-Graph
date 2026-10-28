using System.Collections.Concurrent;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #723: a worker that keeps ticking its heartbeat in the running stage while <c>windowsDone</c> stays frozen fails with
/// <c>worker_progress_stalled</c>, through the same stall prompt as the other stalls, on virtual time (no wall-clock windows).
/// </summary>
public class CloudJobRunnerProgressStallTests
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

    private static CloudJobRunner Runner(FakeGcp gcp, VirtualTimeProvider clock, StallDecision? onStall, TimeSpan? bound = null) => new(gcp, gcp, gcp, gcp, gcp, new InMemoryRunRepository())
    {
        ResultPollInterval = TimeSpan.FromSeconds(10),
        ResultTimeout = TimeSpan.FromHours(3),
        FirstHeartbeatTimeout = TimeSpan.FromHours(1),
        HeartbeatStaleTimeout = TimeSpan.FromHours(1),
        ProgressStallTimeout = bound ?? TimeSpan.FromMinutes(20),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(200),
        TimeProvider = clock,
        PollDelay = (span, token) =>
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(span);
            return Task.CompletedTask;
        },
        OnStall = onStall,
    };

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId)
        => (await gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;

    [Fact]
    public async Task A_ticking_heartbeat_with_frozen_progress_fails_with_the_progress_code_and_the_VM_is_stopped_after_the_bound_on_virtual_time()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInRunning);
        var clock = new VirtualTimeProvider();

        var result = await Runner(gcp, clock, null).RunAsync(TestInputs.Request("job-wedge", Spec("job-wedge")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerProgressStalled);
        (await VmStatusAsync(gcp, "job-wedge")).ShouldBe("STOPPED");
        TimeSpan.FromTicks(clock.GetTimestamp()).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(20));
        TimeSpan.FromTicks(clock.GetTimestamp()).ShouldBeLessThan(TimeSpan.FromMinutes(25), "the progress rule, not the 3 hour result limit, ended it");
    }

    [Fact]
    public async Task The_stall_prompt_is_asked_with_the_progress_code_and_the_choice_is_applied()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInRunning);
        var prompts = new ConcurrentQueue<StallPrompt>();

        var result = await Runner(gcp, new VirtualTimeProvider(), (prompt, _) =>
        {
            prompts.Enqueue(prompt);
            return Task.FromResult(StallChoice.Delete);
        }).RunAsync(TestInputs.Request("job-ask", Spec("job-ask")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var seen = prompts.ShouldHaveSingleItem();
        seen.Code.ShouldBe(RunErrorCodes.WorkerProgressStalled);
        seen.Stage.ShouldBe("running");
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerProgressStalled);
        (await VmStatusAsync(gcp, "job-ask")).ShouldBeNull("Delete means the VM is gone");
    }

    [Fact]
    public async Task A_worker_without_the_progress_fields_that_keeps_ticking_is_not_failed_by_this_rule()
    {
        // HeartbeatingThenDone is an "older worker": no windowsDone, no lastProgressAt.
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);

        var result = await Runner(gcp, new VirtualTimeProvider(), null, bound: TimeSpan.FromSeconds(20)).RunAsync(TestInputs.Request("job-old", Spec("job-old")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
    }
}
