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

        var found = await runs.CancelAndWaitAsync("job-1");

        found.ShouldBeTrue();
        ended.ShouldBeTrue("the driver has finished before the caller writes its own phase");
        runs.IsActive("job-1").ShouldBeFalse();
        task!.IsCompletedSuccessfully.ShouldBeTrue("a cancel the caller asked for is not a fault");
    }

    [Fact]
    public async Task Cancel_of_an_unknown_job_is_a_no_op()
    {
        (await new ActiveRuns().CancelAndWaitAsync("job-none")).ShouldBeFalse();
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
}
