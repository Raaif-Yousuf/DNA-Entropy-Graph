using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #113 (review H2): phase transitions are atomic per job. A driver that READ Running and is then held up before its write must not overwrite the
/// Cancelled another writer (the exit cleanup) committed meanwhile: the write is conditional on the phase that was read, and a terminal phase is never left.
/// </summary>
public class RunRowStoreRaceTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(InMemoryRunRepository Repo, RunRowStore Store)> RigAsync(JobPhase start)
    {
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-1", start, Created), CancellationToken.None);
        return (repo, new RunRowStore(repo, null));
    }

    private static RunRecord Latest(InMemoryRunRepository repo) => RunRowStore.LatestRecord(repo.AllRecordedInOrder, "job-1")!;

    [Theory]
    [InlineData(JobPhase.Finalizing)]
    [InlineData(JobPhase.Failed)]
    [InlineData(JobPhase.Cancelling)]
    public async Task A_driver_that_read_running_cannot_overwrite_a_cancelled_committed_before_its_write(JobPhase lateWrite)
    {
        var (repo, store) = await RigAsync(JobPhase.Running);
        var injected = false;
        repo.BeforeUpsert = async (run, ct) =>
        {
            // The driver has read Running and is about to write; another writer (no in-process lock held: another process, a reconciler) commits Cancelled first.
            if (!injected && run.Phase == lateWrite)
            {
                injected = true;
                await repo.UpsertAsync(new RunRecord("job-1", JobPhase.Cancelled, Created, ErrorCode: RunErrorCodes.StoppedByCleanup), ct);
            }
        };

        await Should.ThrowAsync<InvalidOperationException>(() => store.SetPhaseAsync("job-1", lateWrite, CancellationToken.None));

        injected.ShouldBeTrue();
        var row = Latest(repo);
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.ErrorCode.ShouldBe(RunErrorCodes.StoppedByCleanup);
    }

    [Fact]
    public async Task A_lost_race_to_a_writer_that_already_wrote_the_same_phase_is_re_read_and_leaves_that_row_alone()
    {
        var (repo, store) = await RigAsync(JobPhase.Preparing);
        var injected = false;
        repo.BeforeUpsert = async (run, ct) =>
        {
            if (!injected && run.Phase == JobPhase.Running)
            {
                injected = true;
                // Someone else moved the row to Running (with its own progress) between the driver's read and its write.
                await repo.UpsertAsync(new RunRecord("job-1", JobPhase.Running, Created, LastProgressSeq: 7), ct);
            }
        };

        await store.SetPhaseAsync("job-1", JobPhase.Running, CancellationToken.None);

        injected.ShouldBeTrue();
        var row = Latest(repo);
        row.Phase.ShouldBe(JobPhase.Running);
        row.LastProgressSeq.ShouldBe(7); // the driver's stale-built row was not written over it
        repo.AllRecordedInOrder.Count.ShouldBe(2); // the seed and the other writer's: the driver wrote nothing
    }

    [Fact]
    public async Task A_second_writer_for_the_same_job_waits_until_the_first_has_written()
    {
        var (repo, store) = await RigAsync(JobPhase.Running);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.BeforeUpsert = async (run, ct) =>
        {
            if (run.Phase == JobPhase.Finalizing)
            {
                parked.TrySetResult();
                await release.Task;
            }
        };

        var first = store.SetPhaseAsync("job-1", JobPhase.Finalizing, CancellationToken.None);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        repo.BeforeGetAll = (call, ct) =>
        {
            secondRead.TrySetResult();
            return Task.CompletedTask;
        };
        var second = store.SetPhaseAsync("job-1", JobPhase.Cancelling, CancellationToken.None);

        // A negative proof can only be bounded: the second writer must not even READ while the first holds the row.
        (await Task.WhenAny(secondRead.Task, Task.Delay(300, TestContext.Current.CancellationToken))).ShouldNotBe(secondRead.Task, "the second writer read the row while the first was between its read and its write");

        release.SetResult();
        await Task.WhenAll(first, second);
        Latest(repo).Phase.ShouldBe(JobPhase.Cancelling);
    }
}
