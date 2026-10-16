using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #113 (review H2): phase transitions are atomic per job. A driver that READ Running and is then held up before its write must not overwrite the
/// Cancelled another writer (the exit cleanup) committed meanwhile: the write is conditional on the phase that was read, and a terminal phase is never left.
/// Every test uses its own job id: the per-job lock is process-wide, so a shared id would let one test's parked writer stall another's.
/// </summary>
public class RunRowStoreRaceTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(InMemoryRunRepository Repo, RunRowStore Store, string Job)> RigAsync(JobPhase start)
    {
        var job = "job-" + Guid.NewGuid().ToString("N");
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord(job, start, Created), CancellationToken.None);
        return (repo, new RunRowStore(repo, null), job);
    }

    private static RunRecord Latest(InMemoryRunRepository repo, string job) => RunRowStore.LatestRecord(repo.AllRecordedInOrder, job)!;

    [Theory]
    [InlineData(JobPhase.Finalizing)]
    [InlineData(JobPhase.Failed)]
    [InlineData(JobPhase.Cancelling)]
    public async Task A_driver_that_read_running_cannot_overwrite_a_cancelled_committed_before_its_write(JobPhase lateWrite)
    {
        var (repo, store, job) = await RigAsync(JobPhase.Running);
        var injected = false;
        repo.BeforeUpsert = async (run, ct) =>
        {
            // The driver has read Running and is about to write; another writer (no in-process lock held: another process, a reconciler) commits Cancelled first.
            if (!injected && run.Phase == lateWrite)
            {
                injected = true;
                await repo.UpsertAsync(new RunRecord(job, JobPhase.Cancelled, Created, ErrorCode: RunErrorCodes.StoppedByCleanup), ct);
            }
        };

        await Should.ThrowAsync<InvalidOperationException>(() => store.SetPhaseAsync(job, lateWrite, CancellationToken.None));

        injected.ShouldBeTrue();
        var row = Latest(repo, job);
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.ErrorCode.ShouldBe(RunErrorCodes.StoppedByCleanup);
    }

    [Fact]
    public async Task A_lost_race_to_a_writer_that_already_wrote_the_same_phase_is_re_read_and_leaves_that_row_alone()
    {
        var (repo, store, job) = await RigAsync(JobPhase.Preparing);
        var injected = false;
        repo.BeforeUpsert = async (run, ct) =>
        {
            if (!injected && run.Phase == JobPhase.Running)
            {
                injected = true;
                // Someone else moved the row to Running (with its own progress) between the driver's read and its write.
                await repo.UpsertAsync(new RunRecord(job, JobPhase.Running, Created, LastProgressSeq: 7), ct);
            }
        };

        await store.SetPhaseAsync(job, JobPhase.Running, CancellationToken.None);

        injected.ShouldBeTrue();
        var row = Latest(repo, job);
        row.Phase.ShouldBe(JobPhase.Running);
        row.LastProgressSeq.ShouldBe(7); // the driver's stale-built row was not written over it
        repo.AllRecordedInOrder.Count.ShouldBe(2); // the seed and the other writer's: the driver wrote nothing
    }

    [Fact]
    public async Task An_annotation_that_read_running_cannot_overwrite_a_cancelled_committed_before_its_write()
    {
        var (repo, store, job) = await RigAsync(JobPhase.Running);
        var injected = false;
        repo.BeforeUpsert = async (run, ct) =>
        {
            if (!injected && run.ErrorDetail == "note")
            {
                injected = true;
                await repo.UpsertAsync(new RunRecord(job, JobPhase.Cancelled, Created, ErrorCode: RunErrorCodes.StoppedByCleanup), ct);
            }
        };

        await store.UpdateRowAsync(job, row => row with { ErrorDetail = "note" }, CancellationToken.None);

        injected.ShouldBeTrue();
        var row = Latest(repo, job);
        row.Phase.ShouldBe(JobPhase.Cancelled); // never Running again
        row.ErrorCode.ShouldBe(RunErrorCodes.StoppedByCleanup); // nothing the other writer wrote was lost
        row.ErrorDetail.ShouldBe("note"); // and the annotation was re-applied to the row that is really there
    }

    [Fact]
    public async Task A_second_writer_for_the_same_job_waits_until_the_first_has_written()
    {
        var (repo, store, job) = await RigAsync(JobPhase.Running);
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

        var first = store.SetPhaseAsync(job, JobPhase.Finalizing, CancellationToken.None);
        Task? second = null;
        try
        {
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            repo.BeforeGetAll = (call, ct) =>
            {
                secondRead.TrySetResult();
                return Task.CompletedTask;
            };
            second = store.SetPhaseAsync(job, JobPhase.Cancelling, CancellationToken.None);

            // A negative proof can only be bounded: the second writer must not even READ while the first holds the row.
            (await Task.WhenAny(secondRead.Task, Task.Delay(300, TestContext.Current.CancellationToken))).ShouldNotBe(secondRead.Task, "the second writer read the row while the first was between its read and its write");
        }
        finally
        {
            release.TrySetResult(); // never leave the holder parked, whatever the assertion above did
        }

        await Task.WhenAll(first, second!);
        Latest(repo, job).Phase.ShouldBe(JobPhase.Cancelling);
    }
}
