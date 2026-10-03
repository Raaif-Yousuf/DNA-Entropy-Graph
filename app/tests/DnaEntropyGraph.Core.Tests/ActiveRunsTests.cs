using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #59: one owner of "the task driving this job", shared by the engine and the reconciler, so a cancel always stops and awaits the driver before it writes its own phase.</summary>
public sealed class ActiveRunsTests
{
    [Fact]
    public async Task Cancel_stops_the_driver_waits_for_it_and_forgets_it()
    {
        var runs = new ActiveRuns();
        var started = new TaskCompletionSource();
        var ended = false;
        var task = runs.TryStart("job-1", async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                ended = true;
            }
        });
        (task is null).ShouldBeFalse();
        await started.Task;
        runs.IsActive("job-1").ShouldBeTrue();

        var cancelRan = false;
        await runs.CancelAsync("job-1", () =>
        {
            cancelRan = true;
            ended.ShouldBeTrue("the driver has finished before the cancel writes its own phase");
            return Task.CompletedTask;
        });

        cancelRan.ShouldBeTrue();
        ended.ShouldBeTrue("the driver has finished before the caller writes its own phase");
        runs.IsActive("job-1").ShouldBeFalse();
        task!.IsCompletedSuccessfully.ShouldBeTrue("a cancel the caller asked for is not a fault");
    }

    [Fact]
    public async Task Cancel_of_a_job_with_no_driver_still_runs_the_cancel()
    {
        var cancelRan = false;

        await new ActiveRuns().CancelAsync("job-none", () =>
        {
            cancelRan = true;
            return Task.CompletedTask;
        });

        cancelRan.ShouldBeTrue("the caller then cancels the run itself");
    }

    [Fact]
    public async Task A_second_concurrent_cancel_runs_nothing_and_the_settle_waits_for_the_first()
    {
        var runs = new ActiveRuns();
        var release = new TaskCompletionSource();
        var calls = 0;
        var first = runs.CancelAsync("job-1", async () =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
        });
        var second = runs.CancelAsync("job-1", () =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        second.IsCompleted.ShouldBeFalse("the second cancel waits for the first instead of writing beside it");
        runs.WhenCancelSettledAsync("job-1").IsCompleted.ShouldBeFalse();
        release.SetResult();
        await first;
        await second;

        calls.ShouldBe(1);
        runs.WhenCancelSettledAsync("job-1").IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_start_for_a_job_that_is_still_driven_is_refused()
    {
        var runs = new ActiveRuns();
        var release = new TaskCompletionSource();
        var first = runs.TryStart("job-1", _ => release.Task);

        runs.TryStart("job-1", _ => Task.CompletedTask).ShouldBeNull();

        release.SetResult();
        await first!;
        runs.IsActive("job-1").ShouldBeFalse("a finished driver is removed");
        (runs.TryStart("job-1", _ => Task.CompletedTask) is null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancel_that_throws_still_settles_so_nothing_waits_on_it_and_the_exception_reaches_the_caller()
    {
        var runs = new ActiveRuns();
        var started = new TaskCompletionSource();
        _ = runs.TryStart("job-1", async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await started.Task;
        var release = new TaskCompletionSource();

        var cancel = runs.CancelAsync("job-1", async () =>
        {
            await release.Task;
            throw new InvalidOperationException("no route");
        });
        var settled = runs.WhenCancelSettledAsync("job-1");
        settled.IsCompleted.ShouldBeFalse("the cancel has not ended yet");
        release.SetResult();

        await Should.ThrowAsync<InvalidOperationException>(() => cancel);
        settled.IsCompletedSuccessfully.ShouldBeTrue("a failed cancel still ends the wait");
        runs.WhenCancelSettledAsync("job-1").IsCompletedSuccessfully.ShouldBeTrue("no cancel in progress is not something to wait for");
    }

    [Fact]
    public async Task Started_is_raised_once_the_job_is_registered_and_not_for_a_refused_start()
    {
        // #559 r5: a signal a test (or a screen) waits on instead of polling IsActive. Raised only after IsActive is true, so a cancel that follows finds the driver.
        var runs = new ActiveRuns();
        var raised = new List<(string JobId, bool WasActive)>();
        runs.Started += jobId => raised.Add((jobId, runs.IsActive(jobId)));
        var release = new TaskCompletionSource();

        var first = runs.TryStart("job-1", _ => release.Task);
        runs.TryStart("job-1", _ => Task.CompletedTask).ShouldBeNull();

        raised.ShouldBe([("job-1", true)]);
        release.SetResult();
        await first!;
    }

    [Fact]
    public async Task A_Started_handler_that_throws_does_not_stop_the_driver()
    {
        var runs = new ActiveRuns();
        runs.Started += _ => throw new InvalidOperationException("handler");
        var ran = false;

        var task = runs.TryStart("job-1", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        await task!;

        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task A_Started_handler_that_throws_does_not_starve_the_handlers_after_it()
    {
        var runs = new ActiveRuns();
        var second = new List<string>();
        runs.Started += _ => throw new InvalidOperationException("first handler");
        runs.Started += jobId => second.Add(jobId);

        await runs.TryStart("job-1", _ => Task.CompletedTask)!;

        second.ShouldBe(["job-1"], "every subscriber hears the start, whatever an earlier one did");
    }
}
