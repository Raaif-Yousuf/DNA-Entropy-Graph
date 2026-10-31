using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issue #830 stage 3: a run row remembers the Google account it was made as (<c>Runs.AccountSub</c>), so a later stop, delete or reconcile acts as that account whoever is signed in
/// as current. A row from before the column existed stays null (the current account) until RunAccountBackfill gives it the account signed in at the first launch after the upgrade (Guards.Tests RunAccountBackfillStartupTests).
/// </summary>
public sealed class RunAccountSubTests
{
    [Fact]
    public async Task The_account_a_run_was_made_as_survives_a_relaunch()
    {
        using var paths = new TempPaths();
        var first = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await first.UpsertAsync(new RunRecord("job-a", JobPhase.Running, DateTimeOffset.UtcNow, ProjectId: null, AccountSub: "sub-a"), CancellationToken.None);

        var second = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        var row = (await second.GetAllAsync(CancellationToken.None)).Single();

        row.AccountSub.ShouldBe("sub-a");
    }

    [Fact]
    public async Task A_later_write_from_a_record_without_the_account_never_erases_it()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new RunRecord("job-b", JobPhase.Running, DateTimeOffset.UtcNow, AccountSub: "sub-b"), CancellationToken.None);

        await repository.UpsertAsync(new RunRecord("job-b", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.Phase.ShouldBe(JobPhase.Completed);
        row.AccountSub.ShouldBe("sub-b");
    }

    [Fact]
    public async Task The_time_a_computers_end_was_confirmed_survives_a_relaunch_and_a_stale_writers_record_never_erases_it()
    {
        // Round 4 M-B: the reconciler records it; the runner's own copy of the row (written later, knowing nothing of it) must not wipe it.
        using var paths = new TempPaths();
        var confirmed = new DateTimeOffset(2026, 10, 30, 9, 30, 0, TimeSpan.Zero);
        var first = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await first.UpsertAsync(new RunRecord("job-c", JobPhase.Completed, DateTimeOffset.UtcNow, VmEndConfirmedAt: confirmed), CancellationToken.None);
        await first.UpsertAsync(new RunRecord("job-c", JobPhase.Completed, DateTimeOffset.UtcNow, Notes: null), CancellationToken.None);

        var second = new RunRepository(new SqliteDatabase(paths.DatabasePath));

        (await second.GetAsync("job-c", CancellationToken.None))!.VmEndConfirmedAt.ShouldBe(confirmed);
        (await second.GetAllAsync(CancellationToken.None)).Single().VmEndConfirmedAt.ShouldBe(confirmed);
    }
}
