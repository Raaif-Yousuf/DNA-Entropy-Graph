using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #260 review (r3): a run row with no project must be skipped, never turned into an exception that escapes a run's own finish.</summary>
public class ProjectBucketMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_run_with_no_project_remembers_nothing_and_throws_nothing(string? projectId)
    {
        var runs = new InMemoryRunRepository();
        await runs.UpsertAsync(new RunRecord("job-1", JobPhase.Completed, Now, Target: "cloud", Bucket: "bucket-x"), CancellationToken.None);
        var store = new MemoryProjectStore();

        await ProjectBucketMemory.RememberAsync(runs, store, "job-1", projectId);

        store.Rows.ShouldBeEmpty("a blank project id is never used as a key");
    }

    [Fact]
    public async Task A_run_with_a_project_and_a_bucket_remembers_the_bucket_under_that_project()
    {
        var runs = new InMemoryRunRepository();
        await runs.UpsertAsync(new RunRecord("job-1", JobPhase.Completed, Now, Target: "cloud", Bucket: "bucket-x", ProjectId: "project-a"), CancellationToken.None);
        var store = new MemoryProjectStore();

        await ProjectBucketMemory.RememberAsync(runs, store, "job-1", "project-a");

        store.Rows["project-a"].Bucket.ShouldBe("bucket-x");
    }
}
