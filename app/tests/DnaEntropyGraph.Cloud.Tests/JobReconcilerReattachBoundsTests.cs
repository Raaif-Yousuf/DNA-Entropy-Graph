using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #551 (found by the cold review of #59's reattach cancel signal): the wait for a user cancel is bounded per run, so one cancel that
/// never ends cannot hold back every other run's outcome, and the action a reattach reports can never disagree with the row it ended as.
/// </summary>
public class JobReconcilerReattachBoundsTests
{
    /// <summary>The virtual clock starts before the seeded rows were created; the reconciler only reattaches rows older than its own start, so move it past them first.</summary>
    private static VirtualTimeProvider ClockAfterTheSeededRows()
    {
        var time = new VirtualTimeProvider();
        time.Advance(TimeSpan.FromDays(2));
        return time;
    }

    [Fact]
    public async Task One_runs_cancel_that_never_ends_does_not_hold_back_the_reattach_of_the_others_past_the_settle_deadline()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-hung", JobPhase.Running, vm: true);
        env.Gcp.WithWorker(FakeWorkerMode.Done);
        await env.SeedAsync("job-ok", JobPhase.Running, vm: true);
        var time = ClockAfterTheSeededRows();
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciler = env.Reconciler(time);
        reconciler.CancelSettleArmed = _ => armed.TrySetResult();
        var reattach = reconciler.ReattachAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Active.IsActive("job-hung") && env.Row("job-ok").Phase == JobPhase.Completed);

        // The user cancels job-hung, and the cancel's own work (a gateway call that ignores its token) never returns.
        var neverEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = env.Active.CancelAsync("job-hung", () => neverEnds.Task);
        await armed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        reattach.IsCompleted.ShouldBeFalse("inside the deadline the reattach still waits for the cancel it did not start");
        time.Advance(TimeSpan.FromMinutes(10));
        var outcomes = await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        outcomes.Single(o => o.JobId == "job-ok").FinalPhase.ShouldBe(JobPhase.Completed);
        var hung = outcomes.Single(o => o.JobId == "job-hung");
        hung.Action.ShouldBe(ReattachAction.CancelInterrupted, "the reattach was stopped by a cancel that never finished: it is neither driving the run nor reporting it resumed");
        hung.FinalPhase.ShouldBe(JobPhase.Running, "the cancel has not finished, and the outcome says the row is as it was");
        neverEnds.SetResult();
        await cancel;
    }

    [Fact]
    public async Task A_cancel_that_ends_inside_the_deadline_is_waited_for_and_reported_as_the_row_ends()
    {
        // REGRESSION GUARD: green before #551 too (the old wait was unbounded, so a cancel that ends in time was always waited for). It pins that the
        // new deadline does not cut a legitimate cancel short.
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-slow", JobPhase.Running, vm: true);
        var time = ClockAfterTheSeededRows();
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciler = env.Reconciler(time);
        reconciler.CancelSettleArmed = _ => armed.TrySetResult();
        var reattach = reconciler.ReattachAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Active.IsActive("job-slow"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = env.Active.CancelAsync("job-slow", async () =>
        {
            await release.Task;
            await env.Repo.UpsertAsync(env.Row("job-slow") with { Phase = JobPhase.Cancelled }, CancellationToken.None);
        });
        await armed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        reattach.IsCompleted.ShouldBeFalse("a cancel still running inside the deadline is waited for");
        release.SetResult();
        await cancel;
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.FinalPhase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_Cancelling_row_whose_cancel_could_not_record_its_end_is_not_reported_as_a_finished_cancel()
    {
        // The cancel ran, but the terminal row could not be written (the canceller swallows that and leaves the row as it was): the row stays
        // Cancelling, so the outcome must not claim a finished cancel. Seeded Cancelling, as a killed app leaves it.
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-cut", JobPhase.Cancelling, vm: true);
        env.Repo.BeforeUpsert = (row, _) => JobStateMachine.IsTerminal(row.Phase) ? throw new IOException("disk full") : Task.CompletedTask;

        var outcome = (await env.Reconciler(ClockAfterTheSeededRows()).ReattachAsync(CancellationToken.None)).Single();

        outcome.FinalPhase.ShouldBe(JobPhase.Cancelling, "the terminal write never landed");
        outcome.Action.ShouldBe(ReattachAction.CancelInterrupted, "a cancel whose end was not recorded must not be reported as CancelFinished");
    }

    [Fact]
    public async Task A_shutdown_while_waiting_for_a_user_cancel_is_reported_as_CancelInterrupted_and_does_not_throw()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-shutdown", JobPhase.Running, vm: true);
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciler = env.Reconciler(ClockAfterTheSeededRows());
        reconciler.CancelSettleArmed = _ => armed.TrySetResult();
        using var shutdown = new CancellationTokenSource();
        var reattach = reconciler.ReattachAsync(shutdown.Token);
        await WaitUntilAsync(() => env.Active.IsActive("job-shutdown"));
        var neverEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = env.Active.CancelAsync("job-shutdown", async () =>
        {
            await env.Repo.UpsertAsync(env.Row("job-shutdown") with { Phase = JobPhase.Cancelling }, CancellationToken.None);
            await neverEnds.Task;
        });
        await armed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => env.Row("job-shutdown").Phase == JobPhase.Cancelling);

        await shutdown.CancelAsync();
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.Action.ShouldBe(ReattachAction.CancelInterrupted);
        outcome.FinalPhase.ShouldBe(JobPhase.Cancelling, "the cancel is in flight; the row says so and the next launch finishes it");
        neverEnds.SetResult();
        await cancel;
    }

    [Fact]
    public async Task A_cancel_that_failed_before_ending_the_run_leaves_the_row_non_terminal_and_is_reported_as_CancelInterrupted_not_Resumed()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-failed-cancel", JobPhase.Running, vm: true);
        var reattach = env.Reconciler(ClockAfterTheSeededRows()).ReattachAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Active.IsActive("job-failed-cancel"));

        await Should.ThrowAsync<IOException>(() => env.Active.CancelAsync("job-failed-cancel", async () =>
        {
            await env.Repo.UpsertAsync(env.Row("job-failed-cancel") with { Phase = JobPhase.Cancelling }, CancellationToken.None);
            throw new IOException("the stop call failed");
        }));
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.FinalPhase.ShouldBe(JobPhase.Cancelling);
        outcome.Action.ShouldBe(ReattachAction.CancelInterrupted, "the run is not being driven and its cancel did not end: it was not resumed");
    }

    [Fact]
    public async Task A_shutdown_while_finishing_a_cancel_is_reported_as_CancelInterrupted_with_the_row_still_Cancelling()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-cut-by-shutdown", JobPhase.Cancelling, vm: true);
        // Counting from here, read 1 is the reconciler's own; read 2 is the runner's cancel looking at the row with the reattach's token (the
        // shutdown reaches it), so a shutdown there leaves the cancel unfinished and the driver with an OperationCanceledException.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Repo.BeforeGetAll = async (call, token) =>
        {
            if (call == 2)
            {
                parked.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        using var shutdown = new CancellationTokenSource();
        var reattach = env.Reconciler(ClockAfterTheSeededRows()).ReattachAsync(shutdown.Token);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await shutdown.CancelAsync();
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.Action.ShouldBe(ReattachAction.CancelInterrupted);
        outcome.FinalPhase.ShouldBe(JobPhase.Cancelling, "the cancel never reached its terminal write; the next launch finishes it");
        env.Row("job-cut-by-shutdown").Phase.ShouldBe(JobPhase.Cancelling);
    }

    [Fact]
    public async Task The_settle_deadline_is_the_mutation_timeout_plus_a_margin_so_a_slow_cancel_that_succeeds_is_waited_for()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-edge", JobPhase.Running, vm: true);
        var time = ClockAfterTheSeededRows();
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationTimeout = TimeSpan.FromMinutes(5);
        var reconciler = env.Reconciler(time, mutationTimeout: mutationTimeout);
        reconciler.CancelSettleArmed = _ => armed.TrySetResult();
        var reattach = reconciler.ReattachAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Active.IsActive("job-edge"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = env.Active.CancelAsync("job-edge", async () =>
        {
            await release.Task;
            await env.Repo.UpsertAsync(env.Row("job-edge") with { Phase = JobPhase.Cancelled }, CancellationToken.None);
        });
        await armed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A cancel allowed the full mutation timeout (the longest one delete or stop may take) that succeeds at the very end of it.
        time.Advance(mutationTimeout);
        reattach.IsCompleted.ShouldBeFalse("the report must outlast the longest a legitimate cancel may take");
        release.SetResult();
        await cancel;
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.FinalPhase.ShouldBe(JobPhase.Cancelled);
        outcome.Action.ShouldBe(ReattachAction.CancelFinished);
    }

    [Fact]
    public async Task A_cancel_that_lands_during_the_Failed_write_is_reported_as_the_cancel_not_as_the_failure()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-race", JobPhase.Running, vm: true, tweak: row => row with { ProjectId = null });
        var inWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Repo.BeforeUpsert = async (row, token) =>
        {
            if (row.Phase == JobPhase.Failed)
            {
                inWrite.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reattach = env.Reconciler().ReattachAsync(CancellationToken.None);
        await inWrite.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The user's cancel stops the reattach (which is inside its write of Failed) and then records Cancelled itself.
        await env.Active.CancelAsync("job-race", async () =>
        {
            env.Repo.BeforeUpsert = null;
            await env.Repo.UpsertAsync(env.Row("job-race") with { Phase = JobPhase.Cancelled }, CancellationToken.None);
        });
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.FinalPhase.ShouldBe(JobPhase.Cancelled);
        outcome.Action.ShouldBe(ReattachAction.CancelFinished, "the row is Cancelled, so the action must not claim the run was failed as unrecoverable");
    }

    [Fact]
    public async Task A_user_cancel_whose_delete_failed_is_reported_as_CancelFailed_not_Resumed()
    {
        // Issue #551 review: VmCanceller records Failed/cancel_failed when the VM is not confirmed gone; the reattach the cancel took over must
        // not report Resumed (it was stopped) or CancelFinished (nothing finished) over that row.
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-cancel-failed", JobPhase.Running, vm: true);
        var reattach = env.Reconciler(ClockAfterTheSeededRows()).ReattachAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Active.IsActive("job-cancel-failed"));

        await env.Active.CancelAsync("job-cancel-failed", () => env.Repo.UpsertAsync(
            env.Row("job-cancel-failed") with { Phase = JobPhase.Failed, ErrorCode = RunErrorCodes.CancelFailed, ErrorDetail = "the VM is still there" },
            CancellationToken.None));
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.FinalPhase.ShouldBe(JobPhase.Failed);
        outcome.ErrorCode.ShouldBe(RunErrorCodes.CancelFailed);
        outcome.Action.ShouldBe(ReattachAction.CancelFailed, "the row says the cancel failed, so the action must too");
    }

    [Theory]
    [InlineData(ReattachAction.Resumed, RunErrorCodes.CancelFailed, ReattachAction.CancelFailed)]
    [InlineData(ReattachAction.FailedUnrecoverable, RunErrorCodes.CancelFailed, ReattachAction.CancelFailed)]
    [InlineData(ReattachAction.FailedVmMissing, RunErrorCodes.CancelFailed, ReattachAction.CancelFailed)]
    [InlineData(ReattachAction.CancelFinished, RunErrorCodes.CancelFailed, ReattachAction.CancelFailed)]
    [InlineData(ReattachAction.CancelFinished, null, ReattachAction.CancelFailed)]
    [InlineData(ReattachAction.Resumed, RunErrorCodes.WorkerCrashed, ReattachAction.Resumed)]
    [InlineData(ReattachAction.FailedUnrecoverable, RunErrorCodes.NoProject, ReattachAction.FailedUnrecoverable)]
    public void A_Failed_row_is_named_for_what_failed(ReattachAction underway, string? errorCode, ReattachAction expected)
    {
        JobReconciler.ActionForFinalPhase(underway, JobPhase.Failed, errorCode).ShouldBe(expected);
    }

    [Fact]
    public void The_failed_row_theory_covers_every_action_a_cancel_can_overwrite()
    {
        // Vacuity guard: the theory above must feed every underway action a user cancel can take over.
        var underways = typeof(JobReconcilerReattachBoundsTests).GetMethod(nameof(A_Failed_row_is_named_for_what_failed))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Cast<InlineDataAttribute>()
            .Select(a => (ReattachAction)a.Data![0]!)
            .ToHashSet();

        underways.ShouldBe([ReattachAction.Resumed, ReattachAction.FailedUnrecoverable, ReattachAction.FailedVmMissing, ReattachAction.CancelFinished], ignoreOrder: true);
    }

    [Theory]
    [InlineData(ReattachAction.FailedUnrecoverable, JobPhase.Failed, ReattachAction.FailedUnrecoverable)]
    [InlineData(ReattachAction.FailedVmMissing, JobPhase.Failed, ReattachAction.FailedVmMissing)]
    [InlineData(ReattachAction.FailedUnrecoverable, JobPhase.Cancelled, ReattachAction.CancelFinished)]
    [InlineData(ReattachAction.FailedVmMissing, JobPhase.Cancelled, ReattachAction.CancelFinished)]
    [InlineData(ReattachAction.Resumed, JobPhase.Cancelled, ReattachAction.CancelFinished)]
    [InlineData(ReattachAction.Resumed, JobPhase.Running, ReattachAction.Resumed)]
    [InlineData(ReattachAction.CancelFinished, JobPhase.Cancelling, ReattachAction.CancelInterrupted)]
    [InlineData(ReattachAction.CancelFinished, null, ReattachAction.CancelInterrupted)]
    [InlineData(ReattachAction.CancelFinished, JobPhase.Cancelled, ReattachAction.CancelFinished)]
    [InlineData(ReattachAction.FailedUnrecoverable, JobPhase.Running, ReattachAction.Resumed)]
    public void The_reported_action_is_derived_from_the_phase_the_row_ended_as(ReattachAction underway, JobPhase? finalPhase, ReattachAction expected)
    {
        JobReconciler.ActionForFinalPhase(underway, finalPhase).ShouldBe(expected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("the condition was not met within 10 s");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
