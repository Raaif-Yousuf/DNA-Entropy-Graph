using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>Issue #260: what the app remembers per Google Cloud project, and which projects each account on this PC has chosen.</summary>
public class ProjectSettingsStoreTests
{
    private static (ProjectRepository Repository, ProjectSettingsStore Store) Open(TempPaths paths)
    {
        var repository = new ProjectRepository(new SqliteDatabase(paths.DatabasePath));
        return (repository, new ProjectSettingsStore(repository));
    }

    [Fact]
    public async Task A_remembered_bucket_is_kept_per_project_and_keeps_the_rest_of_the_row()
    {
        using var paths = new TempPaths();
        var (repository, store) = Open(paths);
        await repository.UpsertAsync(new ProjectRecord("project-a", DisplayName: "Teaching", LastGoodZone: "us-central1-a"), CancellationToken.None);
        await store.EnsureAsync("project-b", null, CancellationToken.None);

        await store.RememberBucketAsync("project-a", "bucket-a", CancellationToken.None);
        await store.RememberBucketAsync("project-b", "bucket-b", CancellationToken.None);

        (await store.GetAsync("project-a", CancellationToken.None))!.Bucket.ShouldBe("bucket-a");
        (await store.GetAsync("project-b", CancellationToken.None))!.Bucket.ShouldBe("bucket-b");
        var row = (await repository.GetAllAsync(CancellationToken.None)).Single(r => r.ProjectId == "project-a");
        row.DisplayName.ShouldBe("Teaching");
        row.LastGoodZone.ShouldBe("us-central1-a");
        (await store.GetAsync("never-chosen", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Remembering_a_bucket_and_choosing_a_project_at_the_same_time_lose_neither_write()
    {
        using var paths = new TempPaths();
        var (repository, store) = Open(paths);
        await store.EnsureAsync("project-a", "sub-1", CancellationToken.None);

        for (var i = 0; i < 20; i++)
        {
            await Task.WhenAll(
                store.RememberBucketAsync("project-a", "bucket-" + i, CancellationToken.None),
                store.EnsureAsync("project-a", "sub-1", CancellationToken.None));
        }

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.Bucket.ShouldBe("bucket-19");
        row.AccountSub.ShouldBe("sub-1");
    }

    [Fact]
    public async Task The_known_list_is_the_projects_that_account_chose()
    {
        using var paths = new TempPaths();
        var (_, store) = Open(paths);
        await store.EnsureAsync("mine-1", "sub-1", CancellationToken.None);
        await store.EnsureAsync("mine-2", "sub-1", CancellationToken.None);
        await store.EnsureAsync("theirs", "sub-2", CancellationToken.None);
        await store.EnsureAsync("unowned", null, CancellationToken.None);

        (await store.GetKnownAsync("sub-1", CancellationToken.None)).Select(p => p.ProjectId).ShouldBe(["mine-1", "mine-2"]);
        (await store.GetKnownAsync(null, CancellationToken.None)).ShouldBeEmpty("no signed-in account, no projects to offer");
    }

    [Fact]
    public async Task Two_accounts_that_chose_the_same_project_each_keep_it_in_their_own_list()
    {
        using var paths = new TempPaths();
        var (_, store) = Open(paths);
        await store.EnsureAsync("shared", "sub-1", CancellationToken.None);
        await store.EnsureAsync("shared", "sub-2", CancellationToken.None);

        (await store.GetKnownAsync("sub-1", CancellationToken.None)).Select(p => p.ProjectId).ShouldBe(["shared"], "the second account choosing it must not take it from the first");
        (await store.GetKnownAsync("sub-2", CancellationToken.None)).Select(p => p.ProjectId).ShouldBe(["shared"]);
    }

    [Fact]
    public async Task A_project_row_AccountSub_is_set_once_and_never_rewritten_by_a_later_choice()
    {
        using var paths = new TempPaths();
        var (repository, store) = Open(paths);
        await store.EnsureAsync("shared", null, CancellationToken.None);
        await store.EnsureAsync("shared", "sub-1", CancellationToken.None);
        await store.EnsureAsync("shared", "sub-2", CancellationToken.None);

        (await repository.GetAllAsync(CancellationToken.None)).Single().AccountSub.ShouldBe("sub-1", "set by the first account that chose it, then left alone");
    }

    [Fact]
    public async Task Choosing_a_project_remembers_nothing_but_the_choice()
    {
        using var paths = new TempPaths();
        var (repository, store) = Open(paths);
        await repository.UpsertAsync(new ProjectRecord("project-a", DisplayName: "Teaching", Bucket: "bucket-a", HomeRegionGroup: "us"), CancellationToken.None);

        await store.EnsureAsync("project-a", "sub-1", CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.DisplayName.ShouldBe("Teaching");
        row.Bucket.ShouldBe("bucket-a");
        row.HomeRegionGroup.ShouldBe("us");
    }
}
