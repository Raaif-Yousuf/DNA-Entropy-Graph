using System.Collections.Concurrent;
using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #675: when a stage passes 75 percent of its deadline the runner says "taking longer than usual", and when the heartbeat watch fires it asks
/// the caller to choose Stop, Delete or Keep waiting instead of ending the VM at once. Every test asserts the fake VM as well as the recorded run,
/// because a choice that was ignored (or a run that ended with a VM still billing) is the bug.
/// </summary>
public class CloudJobRunnerStallTests
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

    private static CloudJobRunner StuckRunner(FakeGcp gcp, InMemoryRunRepository repo, StallDecision? onStall, Action<RunNotice>? onNotice = null, TimeSpan? resultTimeout = null) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = resultTimeout ?? TimeSpan.FromSeconds(20),
        FirstHeartbeatTimeout = TimeSpan.FromSeconds(20),
        HeartbeatStaleTimeout = TimeSpan.FromSeconds(20),
        StageDeadlines = new Dictionary<string, TimeSpan> { ["model-loading"] = TimeSpan.FromMilliseconds(80) },
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(200),
        OnStall = onStall,
        OnNotice = onNotice,
    };

    private static CloudJobRunner StaleRunner(FakeGcp gcp, InMemoryRunRepository repo, StallDecision? onStall, Action<RunNotice>? onNotice = null, TimeSpan? resultTimeout = null) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = resultTimeout ?? TimeSpan.FromSeconds(20),
        FirstHeartbeatTimeout = TimeSpan.FromMilliseconds(60),
        HeartbeatStaleTimeout = TimeSpan.FromMilliseconds(60),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(200),
        OnStall = onStall,
        OnNotice = onNotice,
    };

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId)
        => (await gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;

    // ---------------------------------------------------------------- the soft notice

    [Fact]
    public async Task A_stage_past_75_percent_of_its_deadline_raises_one_taking_longer_notice_before_the_run_is_ended()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);
        var repo = new InMemoryRunRepository();
        var notices = new ConcurrentQueue<RunNotice>();

        var result = await StuckRunner(gcp, repo, null, notices.Enqueue).RunAsync(TestInputs.Request("job-slow", Spec("job-slow")), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck, "the notice is soft: without a stall handler the run still ends at the deadline");
        var slow = notices.Where(n => n.Kind == RunNoticeKind.SlowStage).ToList();
        var notice = slow.ShouldHaveSingleItem();
        notice.JobId.ShouldBe("job-slow");
        notice.Stage.ShouldBe("model-loading");
        notice.Deadline.ShouldBe(TimeSpan.FromMilliseconds(80));
        notice.InStage.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(60));
        notice.InStage.ShouldBeLessThan(TimeSpan.FromMilliseconds(80) + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_healthy_run_raises_no_notice()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatingThenDone);
        var notices = new ConcurrentQueue<RunNotice>();

        var result = await StaleRunner(gcp, new InMemoryRunRepository(), null, notices.Enqueue).RunAsync(TestInputs.Request("job-ok", Spec("job-ok")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notice_handler_that_throws_never_breaks_the_run()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);

        var result = await StuckRunner(gcp, new InMemoryRunRepository(), null, _ => throw new InvalidOperationException("a broken UI")).RunAsync(TestInputs.Request("job-throw", Spec("job-throw")), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck);
        (await VmStatusAsync(gcp, "job-throw")).ShouldBe("STOPPED");
    }

    // ---------------------------------------------------------------- the three-way choice

    [Fact]
    public async Task Delete_ends_the_run_with_the_stuck_code_and_deletes_the_VM_even_though_the_run_chose_stop()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);
        var repo = new InMemoryRunRepository();
        StallPrompt? seen = null;

        var result = await StuckRunner(gcp, repo, (prompt, _) =>
        {
            seen = prompt;
            return Task.FromResult(StallChoice.Delete);
        }).RunAsync(TestInputs.Request("job-del", Spec("job-del"), afterTask: AfterTaskAction.Stop), CancellationToken.None);

        seen.ShouldNotBeNull();
        seen.JobId.ShouldBe("job-del");
        seen.Code.ShouldBe(RunErrorCodes.WorkerStageStuck);
        seen.Stage.ShouldBe("model-loading");
        seen.Message.ShouldNotBeNullOrWhiteSpace();
        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck);
        (await VmStatusAsync(gcp, "job-del")).ShouldBeNull("Delete means the VM is gone");
    }

    [Fact]
    public async Task Stop_stops_the_VM_even_though_the_run_chose_delete()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);

        var result = await StuckRunner(gcp, new InMemoryRunRepository(), (_, _) => Task.FromResult(StallChoice.Stop))
            .RunAsync(TestInputs.Request("job-stop", Spec("job-stop"), afterTask: AfterTaskAction.Delete), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck);
        (await VmStatusAsync(gcp, "job-stop")).ShouldBe("STOPPED", "Stop means stopped, not the run's own delete choice");
    }

    [Fact]
    public async Task Keep_waiting_does_not_end_the_VM_and_asks_again_only_after_a_fresh_allowance()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);
        var calls = new List<(DateTimeOffset At, string? VmStatus)>();

        var result = await StuckRunner(gcp, new InMemoryRunRepository(), async (prompt, _) =>
        {
            calls.Add((DateTimeOffset.UtcNow, await VmStatusAsync(gcp, prompt.JobId)));
            return calls.Count == 1 ? StallChoice.KeepWaiting : StallChoice.Stop;
        }).RunAsync(TestInputs.Request("job-keep", Spec("job-keep")), CancellationToken.None);

        calls.Count.ShouldBe(2);
        calls[0].VmStatus.ShouldBe("RUNNING");
        calls[1].VmStatus.ShouldBe("RUNNING", "Keep waiting must not have ended the VM");
        (calls[1].At - calls[0].At).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(60), "the stage got a fresh 80 ms allowance, not an immediate second prompt");
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck);
        (await VmStatusAsync(gcp, "job-keep")).ShouldBe("STOPPED");
    }

    [Fact]
    public async Task While_the_choice_is_pending_the_prompt_is_not_repeated_and_the_VM_stays_up_until_the_result_limit_which_cancels_the_prompt()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);
        var calls = 0;
        CancellationToken promptToken = default;

        var result = await StuckRunner(
            gcp,
            new InMemoryRunRepository(),
            async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                promptToken = token;
                await Task.Delay(Timeout.Infinite, token);
                return StallChoice.KeepWaiting;
            },
            resultTimeout: TimeSpan.FromMilliseconds(400)).RunAsync(TestInputs.Request("job-pending", Spec("job-pending")), CancellationToken.None);

        calls.ShouldBe(1, "one open prompt per stall");
        result.FailureCode.ShouldBe(RunErrorCodes.ResultTimeout, "an unanswered prompt never extends the run past its limit (Hard Rule 11)");
        promptToken.IsCancellationRequested.ShouldBeTrue("the prompt is withdrawn when the run ends");
        (await VmStatusAsync(gcp, "job-pending")).ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_stale_heartbeat_prompts_too_and_the_choice_is_applied()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning);
        StallPrompt? seen = null;

        var result = await StaleRunner(gcp, new InMemoryRunRepository(), (prompt, _) =>
        {
            seen = prompt;
            return Task.FromResult(StallChoice.Delete);
        }).RunAsync(TestInputs.Request("job-stale", Spec("job-stale")), CancellationToken.None);

        seen.ShouldNotBeNull().Code.ShouldBe(RunErrorCodes.WorkerHeartbeatStale);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerHeartbeatStale);
        (await VmStatusAsync(gcp, "job-stale")).ShouldBeNull();
    }

    [Fact]
    public async Task A_box_with_no_GPU_is_never_offered_a_choice_it_is_deleted_at_once()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.NoGpuFirstLine);
        var calls = 0;

        var result = await StaleRunner(gcp, new InMemoryRunRepository(), (_, _) =>
        {
            calls++;
            return Task.FromResult(StallChoice.KeepWaiting);
        }).RunAsync(TestInputs.Request("job-nogpu", Spec("job-nogpu")), CancellationToken.None);

        calls.ShouldBe(0);
        result.FailureCode.ShouldBe(RunErrorCodes.GpuNotVisible);
        (await VmStatusAsync(gcp, "job-nogpu")).ShouldBeNull();
    }

    [Fact]
    public async Task A_stall_handler_that_throws_falls_back_to_ending_the_VM_per_the_runs_own_choice()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);

        var result = await StuckRunner(gcp, new InMemoryRunRepository(), (_, _) => throw new InvalidOperationException("a broken UI"))
            .RunAsync(TestInputs.Request("job-broken", Spec("job-broken")), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck);
        (await VmStatusAsync(gcp, "job-broken")).ShouldBe("STOPPED", "no choice made means the default end, never a VM left billing");
    }

    [Fact]
    public async Task A_heartbeat_that_resumes_while_the_prompt_is_open_withdraws_the_prompt_and_says_so()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning);
        var notices = new ConcurrentQueue<RunNotice>();
        CancellationToken promptToken = default;
        var statusKey = "jobs/job-wake/status.json";
        var bucket = FakeGcp.BucketName(Project);

        var result = await StaleRunner(
            gcp,
            new InMemoryRunRepository(),
            async (_, token) =>
            {
                promptToken = token;
                // The worker wakes: a newer heartbeat lands while the user is still deciding.
                gcp.PutObject(bucket, statusKey, Encoding.UTF8.GetBytes("""{"schema":1,"jobId":"job-wake","stage":"running","heartbeatSeq":99,"updatedAt":"2030-01-01T00:00:00Z","worker":{"version":"1.0.0"}}"""));
                await Task.Delay(Timeout.Infinite, token);
                return StallChoice.KeepWaiting;
            },
            notices.Enqueue,
            resultTimeout: TimeSpan.FromMilliseconds(300)).RunAsync(TestInputs.Request("job-wake", Spec("job-wake")), CancellationToken.None);

        promptToken.IsCancellationRequested.ShouldBeTrue();
        notices.Select(n => n.Kind).ShouldContain(RunNoticeKind.StallCleared);
        notices.Select(n => n.Kind).ShouldContain(RunNoticeKind.StallDetected);
        result.FailureCode.ShouldNotBe(RunErrorCodes.WorkerHeartbeatStale, "the heartbeat recovered, so the stale verdict was withdrawn");
    }
}
