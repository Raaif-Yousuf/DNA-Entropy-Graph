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
