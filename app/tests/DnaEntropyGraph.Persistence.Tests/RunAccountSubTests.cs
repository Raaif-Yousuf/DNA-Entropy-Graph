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
    public async Task A_later_write_naming_another_account_never_rewrites_the_one_already_set()
    {
        // Issue #260 review: the account a run was made as is fixed for the run's whole life; a stop, delete or reconcile acts as that account.
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new RunRecord("job-c", JobPhase.Running, DateTimeOffset.UtcNow, AccountSub: "sub-c"), CancellationToken.None);

        await repository.UpsertAsync(new RunRecord("job-c", JobPhase.Completed, DateTimeOffset.UtcNow, AccountSub: "sub-other"), CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.Phase.ShouldBe(JobPhase.Completed);
        row.AccountSub.ShouldBe("sub-c");
    }

    [Fact]
    public async Task A_row_with_no_account_takes_the_first_one_a_write_names()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new RunRecord("job-d", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);

        await repository.UpsertAsync(new RunRecord("job-d", JobPhase.Running, DateTimeOffset.UtcNow, AccountSub: "sub-d"), CancellationToken.None);

        (await repository.GetAllAsync(CancellationToken.None)).Single().AccountSub.ShouldBe("sub-d");
    }
}
