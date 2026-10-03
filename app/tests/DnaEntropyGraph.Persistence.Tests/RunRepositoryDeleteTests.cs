using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class RunRepositoryDeleteTests
{
    [Fact]
    public async Task Deleting_a_run_removes_only_that_row()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new RunRecord("keep", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.UpsertAsync(new RunRecord("drop", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);

        await repository.DeleteAsync("drop", CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);
        all.Select(r => r.JobId).ShouldBe(["keep"]);
    }

    [Fact]
    public async Task Deleting_an_unknown_run_is_not_an_error()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));

        await repository.DeleteAsync("nope", CancellationToken.None);

        (await repository.GetAllAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}
