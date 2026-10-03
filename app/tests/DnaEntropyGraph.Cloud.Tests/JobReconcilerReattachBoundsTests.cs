using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Guards.Tests;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #551 (found by the cold review of #59's reattach cancel signal): the wait for a user cancel is bounded per run, so one cancel that
/// never ends cannot hold back every other run's outcome, and the action a reattach reports can never disagree with the row it ended as.
/// </summary>
public class JobReconcilerReattachBoundsTests
{
    private static readonly DateTimeOffset Launch = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task One_runs_cancel_that_never_ends_does_not_hold_back_the_reattach_of_the_others_past_the_settle_deadline()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-hung", JobPhase.Running, vm: true);
        env.Gcp.WithWorker(FakeWorkerMode.Done);
        await env.SeedAsync("job-ok", JobPhase.Running, vm: true);
        var time = new VirtualTimeProvider(Launch);
        var reattach = env.Reconciler(time).ReattachAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Active.IsActive("job-hung") && env.Row("job-ok").Phase == JobPhase.Completed);

        // The user cancels job-hung, and the cancel's own work (a gateway call that ignores its token) never returns.
        var neverEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = env.Active.CancelAsync("job-hung", () => neverEnds.Task);
        await WaitUntilAsync(() => time.PendingTimers > 0);

        reattach.IsCompleted.ShouldBeFalse("inside the deadline the reattach still waits for the cancel it did not start");
        time.Advance(TimeSpan.FromMinutes(10));
        var outcomes = await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        outcomes.Single(o => o.JobId == "job-ok").FinalPhase.ShouldBe(JobPhase.Completed);
        var hung = outcomes.Single(o => o.JobId == "job-hung");
        (hung.FinalPhase is null || !JobStateMachine.IsTerminal(hung.FinalPhase.Value)).ShouldBeTrue("the cancel has not finished, and the outcome says the row is not terminal");
        neverEnds.SetResult();
        await cancel;
    }

    [Fact]
    public async Task A_cancel_that_ends_inside_the_deadline_is_waited_for_and_reported_as_the_row_ends()
    {
        var env = new JobReconcilerTests.Env(new FakeGcp().WithWorker(FakeWorkerMode.Never)) { ResultTimeout = TimeSpan.FromMinutes(5) };
        await env.SeedAsync("job-slow", JobPhase.Running, vm: true);
        var time = new VirtualTimeProvider(Launch);
        var reattach = env.Reconciler(time).ReattachAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Active.IsActive("job-slow"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = env.Active.CancelAsync("job-slow", async () =>
        {
            await release.Task;
            await env.Repo.UpsertAsync(env.Row("job-slow") with { Phase = JobPhase.Cancelled }, CancellationToken.None);
        });
        await WaitUntilAsync(() => time.PendingTimers > 0);

        time.Advance(TimeSpan.FromSeconds(30));
        reattach.IsCompleted.ShouldBeFalse("a cancel still running inside the deadline is waited for");
        release.SetResult();
        await cancel;
        var outcome = (await reattach.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Single();

        outcome.FinalPhase.ShouldBe(JobPhase.Cancelled);
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
    [InlineData(ReattachAction.FailedUnrecoverable, JobPhase.Running, ReattachAction.Resumed)]
    public void The_reported_action_is_derived_from_the_phase_the_row_ended_as(ReattachAction underway, JobPhase finalPhase, ReattachAction expected)
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
