using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class ProjectRepositoryTests
{
    [Fact]
    public async Task Upserted_project_is_returned_by_get_all()
    {
        using var paths = new TempPaths();
        var repository = new ProjectRepository(new SqliteDatabase(paths.DatabasePath));
        var project = new ProjectRecord("proj-1", DisplayName: "Lab project", BillingEnabled: true, IsActive: true);

        await repository.UpsertAsync(project, CancellationToken.None);
        var all = await repository.GetAllAsync(CancellationToken.None);

        var row = all.ShouldHaveSingleItem();
        row.ProjectId.ShouldBe("proj-1");
        row.DisplayName.ShouldBe("Lab project");
        row.BillingEnabled.ShouldBe(true);
        row.IsActive.ShouldBe(true);
    }

    [Fact]
    public async Task Upserting_the_same_project_id_again_replaces_it_rather_than_duplicating()
    {
        using var paths = new TempPaths();
        var repository = new ProjectRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new ProjectRecord("proj-2", LastGoodZone: "us-central1-a"), CancellationToken.None);
        await repository.UpsertAsync(new ProjectRecord("proj-2", LastGoodZone: "us-central1-b"), CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        all.Count(p => p.ProjectId == "proj-2").ShouldBe(1);
        all.Single(p => p.ProjectId == "proj-2").LastGoodZone.ShouldBe("us-central1-b");
    }

    [Fact]
    public async Task Ensure_inserts_a_bare_row_for_an_unknown_project_id()
    {
        using var paths = new TempPaths();
        var repository = new ProjectRepository(new SqliteDatabase(paths.DatabasePath));

        await repository.EnsureAsync("proj-new", CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).ShouldHaveSingleItem();
        row.ProjectId.ShouldBe("proj-new");
        row.DisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task Ensure_never_overwrites_a_richer_existing_row()
    {
        using var paths = new TempPaths();
        var repository = new ProjectRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new ProjectRecord("proj-rich", DisplayName: "Lab project", Bucket: "b-1", BillingEnabled: true), CancellationToken.None);

        await repository.EnsureAsync("proj-rich", CancellationToken.None);
        await repository.EnsureAsync("proj-rich", CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).ShouldHaveSingleItem();
        row.DisplayName.ShouldBe("Lab project");
        row.Bucket.ShouldBe("b-1");
        row.BillingEnabled.ShouldBe(true);
    }

    [Fact]
    public async Task Ensure_makes_a_run_row_for_that_project_insertable_under_foreign_keys()
    {
        using var paths = new TempPaths();
        var database = new SqliteDatabase(paths.DatabasePath);
        await new ProjectRepository(database).EnsureAsync("proj-fk", CancellationToken.None);

        await new RunRepository(database).UpsertAsync(
            new RunRecord("20260101-000000-abcdef", JobPhase.Draft, DateTimeOffset.UtcNow, ProjectId: "proj-fk"),
            CancellationToken.None);

        (await new RunRepository(database).GetAllAsync(CancellationToken.None)).ShouldHaveSingleItem().ProjectId.ShouldBe("proj-fk");
    }

    [Fact]
    public async Task A_project_survives_a_relaunch()
    {
        using var paths = new TempPaths();
        var firstLaunch = new ProjectRepository(new SqliteDatabase(paths.DatabasePath));
        await firstLaunch.UpsertAsync(new ProjectRecord("proj-3"), CancellationToken.None);

        var secondLaunch = new ProjectRepository(new SqliteDatabase(paths.DatabasePath));
        var all = await secondLaunch.GetAllAsync(CancellationToken.None);

        all.ShouldContain(p => p.ProjectId == "proj-3");
    }
}
