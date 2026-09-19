using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class CloudResourceRepositoryTests
{
    [Fact]
    public async Task Upserted_resource_is_returned_by_get_all()
    {
        using var paths = new TempPaths();
        var repository = new CloudResourceRepository(new SqliteDatabase(paths.DatabasePath));
        var resource = new CloudResourceRecord("vm", "deg-job-1", ProjectId: null, CreatedAt: DateTimeOffset.UtcNow, GpuType: "L4");

        await repository.UpsertAsync(resource, CancellationToken.None);
        var all = await repository.GetAllAsync(CancellationToken.None);

        all.ShouldContain(r => r.Kind == "vm" && r.Name == "deg-job-1" && r.GpuType == "L4");
    }

    /// <summary>The table's own UNIQUE(Kind, ProjectId, Name) is the upsert key (Hard Rule 9: discovery by label, never a fixed name, but the local cache still needs one identity per real resource).</summary>
    [Fact]
    public async Task Upserting_the_same_kind_project_and_name_again_replaces_it_rather_than_duplicating()
    {
        using var paths = new TempPaths();
        var repository = new CloudResourceRepository(new SqliteDatabase(paths.DatabasePath));
        var createdAt = DateTimeOffset.UtcNow;
        await repository.UpsertAsync(new CloudResourceRecord("vm", "deg-job-2", "proj-1", createdAt, LastKnownState: "RUNNING"), CancellationToken.None);
        await repository.UpsertAsync(new CloudResourceRecord("vm", "deg-job-2", "proj-1", createdAt, LastKnownState: "STOPPED"), CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        all.Count(r => r.Kind == "vm" && r.ProjectId == "proj-1" && r.Name == "deg-job-2").ShouldBe(1);
        all.Single(r => r.Name == "deg-job-2").LastKnownState.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_resource_survives_a_relaunch()
    {
        using var paths = new TempPaths();
        var firstLaunch = new CloudResourceRepository(new SqliteDatabase(paths.DatabasePath));
        await firstLaunch.UpsertAsync(new CloudResourceRecord("bucket", "deg-123-abc", null, DateTimeOffset.UtcNow), CancellationToken.None);

        var secondLaunch = new CloudResourceRepository(new SqliteDatabase(paths.DatabasePath));
        var all = await secondLaunch.GetAllAsync(CancellationToken.None);

        all.ShouldContain(r => r.Kind == "bucket" && r.Name == "deg-123-abc");
    }
}
