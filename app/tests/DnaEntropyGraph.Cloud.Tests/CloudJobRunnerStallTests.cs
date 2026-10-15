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

    /// <summary>
    /// The runner on VIRTUAL time (#719): every deadline, stage allowance and answer timeout is measured on <paramref name="clock"/>, which moves only when the runner sleeps between
    /// looks, so a loaded machine cannot stretch or shrink a window. <paramref name="gate"/> runs before each sleep: a test whose handler must have acted before time moves on
    /// (the handler runs on its own task) awaits its signal there instead of racing the poll loop.
    /// </summary>
    private static CloudJobRunner Virtual(FakeGcp gcp, InMemoryRunRepository repo, VirtualTimeProvider clock, StallDecision? onStall, Action<RunNotice>? onNotice, TimeSpan resultTimeout, bool stuck, Func<Task>? gate = null, TimeSpan? answerTimeout = null) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = resultTimeout,
        FirstHeartbeatTimeout = stuck ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(60),
        HeartbeatStaleTimeout = stuck ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(60),
        StageDeadlines = stuck ? new Dictionary<string, TimeSpan> { ["model-loading"] = TimeSpan.FromMilliseconds(80) } : null,
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(200),
        StallAnswerTimeout = answerTimeout ?? TimeSpan.FromMinutes(10),
        TimeProvider = clock,
        PollDelay = async (span, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (gate is not null)
            {
                await gate().ConfigureAwait(false);
            }

            clock.Advance(span);
        },
        OnStall = onStall,
        OnNotice = onNotice,
    };

    private static TimeSpan Elapsed(VirtualTimeProvider clock) => TimeSpan.FromTicks(clock.GetTimestamp());

    private static CloudJobRunner StuckRunner(FakeGcp gcp, InMemoryRunRepository repo, StallDecision? onStall, Action<RunNotice>? onNotice = null, TimeSpan? resultTimeout = null, VirtualTimeProvider? clock = null, Func<Task>? gate = null)
        => Virtual(gcp, repo, clock ?? new VirtualTimeProvider(), onStall, onNotice, resultTimeout ?? TimeSpan.FromSeconds(20), stuck: true, gate);

    private static CloudJobRunner StaleRunner(FakeGcp gcp, InMemoryRunRepository repo, StallDecision? onStall, Action<RunNotice>? onNotice = null, TimeSpan? resultTimeout = null, VirtualTimeProvider? clock = null, Func<Task>? gate = null)
        => Virtual(gcp, repo, clock ?? new VirtualTimeProvider(), onStall, onNotice, resultTimeout ?? TimeSpan.FromSeconds(20), stuck: false, gate);

    /// <summary>A gate that, once virtual time has passed <paramref name="after"/>, waits for <paramref name="signal"/> (the handler's own go-ahead) before time moves on.</summary>
    private static Func<Task> WaitForHandler(VirtualTimeProvider clock, TimeSpan after, TaskCompletionSource signal)
        => () => Elapsed(clock) >= after ? signal.Task.WaitAsync(TimeSpan.FromSeconds(30)) : Task.CompletedTask;

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
        var clock = new VirtualTimeProvider();

        var result = await StuckRunner(gcp, new InMemoryRunRepository(), async (prompt, _) =>
        {
            calls.Add((clock.GetUtcNow(), await VmStatusAsync(gcp, prompt.JobId)));
            return calls.Count == 1 ? StallChoice.KeepWaiting : StallChoice.Stop;
        }, clock: clock).RunAsync(TestInputs.Request("job-keep", Spec("job-keep")), CancellationToken.None);

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
        var clock = new VirtualTimeProvider();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await StuckRunner(
            gcp,
            new InMemoryRunRepository(),
            async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                promptToken = token;
                asked.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return StallChoice.KeepWaiting;
            },
            resultTimeout: TimeSpan.FromMilliseconds(400),
            clock: clock,
            gate: WaitForHandler(clock, TimeSpan.FromMilliseconds(80), asked)).RunAsync(TestInputs.Request("job-pending", Spec("job-pending")), CancellationToken.None);

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
        var clock = new VirtualTimeProvider();
        var woke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await StaleRunner(
            gcp,
            new InMemoryRunRepository(),
            async (_, token) =>
            {
                promptToken = token;
                // The worker wakes: a newer heartbeat lands while the user is still deciding.
                gcp.PutObject(bucket, statusKey, Encoding.UTF8.GetBytes("""{"schema":1,"jobId":"job-wake","stage":"running","heartbeatSeq":99,"updatedAt":"2030-01-01T00:00:00Z","worker":{"version":"1.0.0"}}"""));
                woke.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return StallChoice.KeepWaiting;
            },
            notices.Enqueue,
            resultTimeout: TimeSpan.FromMilliseconds(300),
            clock: clock,
            gate: WaitForHandler(clock, TimeSpan.FromMilliseconds(60), woke)).RunAsync(TestInputs.Request("job-wake", Spec("job-wake")), CancellationToken.None);

        promptToken.IsCancellationRequested.ShouldBeTrue();
        notices.Select(n => n.Kind).ShouldContain(RunNoticeKind.StallCleared);
        notices.Select(n => n.Kind).ShouldContain(RunNoticeKind.StallDetected);
        result.FailureCode.ShouldNotBe(RunErrorCodes.WorkerHeartbeatStale, "the heartbeat recovered, so the stale verdict was withdrawn");
    }

    // ---------------------------------------------------------------- #675 review

    [Fact]
    public async Task A_no_GPU_verdict_while_a_stall_question_is_open_wins_the_VM_is_deleted_at_once_and_the_question_is_withdrawn()
    {
        // The worker is up with a stale heartbeat and its GPU is neither confirmed nor denied: the stall question opens. The handler (a person reading a dialog) then
        // sees the worker's real first line arrive, "no GPU detected": that verdict must not be swallowed because a question is open.
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.HeartbeatStopsAfterRunning).WithWorkerGpuLineMissing();
        var bucket = await gcp.EnsureBucketAsync(Project, CancellationToken.None);
        var noGpuLine = "{\"seq\":2,\"stage\":\"restoring-cache\",\"level\":\"notice\",\"message\":\"worker starting; GPU: no GPU detected; free disk 90 GB\"}\n";
        var withdrawn = new TaskCompletionSource();
        var notices = new ConcurrentQueue<RunNotice>();
        var clock = new VirtualTimeProvider();
        var planted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await StaleRunner(
            gcp,
            new InMemoryRunRepository(),
            async (_, token) =>
            {
                gcp.PutObject(bucket, "jobs/job-race/progress.jsonl", Encoding.UTF8.GetBytes(noGpuLine));
                planted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    withdrawn.TrySetResult();
                    throw;
                }

                return StallChoice.KeepWaiting;
            },
            notices.Enqueue,
            clock: clock,
            gate: WaitForHandler(clock, TimeSpan.FromMilliseconds(60), planted)).RunAsync(TestInputs.Request("job-race", Spec("job-race")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FailureCode.ShouldBe(RunErrorCodes.GpuNotVisible);
        (await VmStatusAsync(gcp, "job-race")).ShouldBeNull("a box with no GPU is deleted, not stopped and not kept waiting");
        await withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        notices.Select(n => n.Kind).ShouldContain(RunNoticeKind.StallCleared);
    }

    [Fact]
    public async Task A_stall_handler_that_blocks_its_thread_does_not_hang_the_run_the_question_times_out_and_the_run_ends_as_it_chose()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);
        using var release = new ManualResetEventSlim();
        var request = TestInputs.Request("job-block", Spec("job-block"), afterTask: AfterTaskAction.Stop);
        var runner = Virtual(gcp, new InMemoryRunRepository(), new VirtualTimeProvider(), (_, _) =>
        {
            release.Wait();
            return Task.FromResult(StallChoice.KeepWaiting);
        }, null, TimeSpan.FromSeconds(20), stuck: true, answerTimeout: TimeSpan.FromMilliseconds(100));

        try
        {
            var result = await runner.RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck);
            (await VmStatusAsync(gcp, "job-block")).ShouldBe("STOPPED", "no answer: the run ends the way it chose");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task The_production_default_answers_a_stall_at_once_with_no_UI_and_the_run_ends_as_it_chose()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading);

        var result = await StuckRunner(gcp, new InMemoryRunRepository(), StallDecisions.EndAsRunChose).RunAsync(TestInputs.Request("job-default", Spec("job-default")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FailureCode.ShouldBe(RunErrorCodes.WorkerStageStuck);
        (await VmStatusAsync(gcp, "job-default")).ShouldBe("STOPPED");
    }
}
