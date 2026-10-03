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
        hung.Action.ShouldBe(ReattachAction.Resumed, "the reattach was resuming the run when the cancel took over, and the cancel never finished");
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
