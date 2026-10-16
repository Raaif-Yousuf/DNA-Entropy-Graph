using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issue #113 (review H2): a phase write that is conditional on the phase the writer READ, so a writer that read Running can never
/// overwrite a Cancelled another writer committed in between.
/// </summary>
public class RunRepositoryConditionalUpsertTests
{
    private static RunRepository Open(TempPaths paths) => new(new SqliteDatabase(paths.DatabasePath));

    [Fact]
    public async Task A_write_whose_expected_phase_matches_the_stored_phase_is_committed()
    {
        using var paths = new TempPaths();
        var repository = Open(paths);
        await repository.UpsertAsync(new RunRecord("job-1", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);

        var written = await repository.TryUpsertIfPhaseAsync(new RunRecord("job-1", JobPhase.Finalizing, DateTimeOffset.UtcNow), JobPhase.Running, CancellationToken.None);

        written.ShouldBeTrue();
        (await repository.GetAllAsync(CancellationToken.None)).Single().Phase.ShouldBe(JobPhase.Finalizing);
    }

    [Fact]
    public async Task A_write_whose_expected_phase_is_stale_changes_nothing_and_says_so()
    {
        using var paths = new TempPaths();
        var repository = Open(paths);
        await repository.UpsertAsync(new RunRecord("job-1", JobPhase.Cancelled, DateTimeOffset.UtcNow, ErrorCode: "cleanup_stopped"), CancellationToken.None);

        var written = await repository.TryUpsertIfPhaseAsync(new RunRecord("job-1", JobPhase.Finalizing, DateTimeOffset.UtcNow), JobPhase.Running, CancellationToken.None);

        written.ShouldBeFalse();
        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.ErrorCode.ShouldBe("cleanup_stopped");
    }

    [Fact]
    public async Task A_write_that_expects_no_row_inserts_only_when_there_is_none()
    {
        using var paths = new TempPaths();
        var repository = Open(paths);

        (await repository.TryUpsertIfPhaseAsync(new RunRecord("job-1", JobPhase.Validating, DateTimeOffset.UtcNow), null, CancellationToken.None)).ShouldBeTrue();
        (await repository.TryUpsertIfPhaseAsync(new RunRecord("job-1", JobPhase.Failed, DateTimeOffset.UtcNow), null, CancellationToken.None)).ShouldBeFalse();

        (await repository.GetAllAsync(CancellationToken.None)).Single().Phase.ShouldBe(JobPhase.Validating);
    }

    [Fact]
    public async Task A_write_that_expects_a_row_never_resurrects_one_that_was_removed()
    {
        using var paths = new TempPaths();
        var repository = Open(paths);

        var written = await repository.TryUpsertIfPhaseAsync(new RunRecord("gone", JobPhase.Finalizing, DateTimeOffset.UtcNow), JobPhase.Running, CancellationToken.None);

        written.ShouldBeFalse();
        (await repository.GetAllAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_writer_forced_into_the_gap_between_the_first_writers_read_and_write_cannot_both_win()
    {
        using var paths = new TempPaths();
        var repository = Open(paths);
        await repository.UpsertAsync(new RunRecord("job-1", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        Task<bool>? second = null;
        var forced = false;
        repository.BetweenPhaseReadAndWrite = async () =>
        {
            if (forced)
            {
                return;
            }

            forced = true;
            // The first writer has read Running and has not written. A plain read-then-upsert lets this second writer complete in the gap (both win);
            // a transaction holding the write lock makes it wait, and this gives it every chance to finish first.
            second = Task.Run(() => repository.TryUpsertIfPhaseAsync(new RunRecord("job-1", JobPhase.Cancelling, DateTimeOffset.UtcNow), JobPhase.Running, CancellationToken.None), TestContext.Current.CancellationToken);
            await Task.WhenAny(second, Task.Delay(1500, TestContext.Current.CancellationToken));
        };

        var first = await repository.TryUpsertIfPhaseAsync(new RunRecord("job-1", JobPhase.Finalizing, DateTimeOffset.UtcNow), JobPhase.Running, CancellationToken.None);
        var secondWon = await second!;

        forced.ShouldBeTrue();
        (first ^ secondWon).ShouldBeTrue("exactly one of the two writers that read Running may win");
        (await repository.GetAllAsync(CancellationToken.None)).Single().Phase.ShouldBe(first ? JobPhase.Finalizing : JobPhase.Cancelling);
    }

    [Fact]
    public async Task Of_many_writers_that_all_read_the_same_phase_exactly_one_wins()
    {
        using var paths = new TempPaths();
        var repository = Open(paths);
        await repository.UpsertAsync(new RunRecord("job-1", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => repository.TryUpsertIfPhaseAsync(
            new RunRecord("job-1", i % 2 == 0 ? JobPhase.Finalizing : JobPhase.Cancelling, DateTimeOffset.UtcNow), JobPhase.Running, CancellationToken.None), TestContext.Current.CancellationToken)));

        results.Count(r => r).ShouldBe(1);
    }
}
