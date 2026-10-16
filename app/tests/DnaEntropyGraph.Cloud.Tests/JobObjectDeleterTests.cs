using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issues #262 and #534: deleting one job's cloud copy by prefix, through the real deleter over FakeGcp.</summary>
public sealed class JobObjectDeleterTests
{
    private const string Bucket = "deg-123-abcdef";
    private const string JobId = "20261003-aaaa";
    private static readonly string Prefix = WorkerManifestBuilder.JobPrefix(JobId);
    private readonly FakeGcp _gcp = new();

    private JobObjectDeleter Make() => new(_gcp);

    private void Seed(string jobId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _gcp.PutObject(Bucket, $"jobs/{jobId}/out/file{i}.bin", [1, 2, 3]);
        }
    }

    [Fact]
    public void Is_available_because_a_real_gateway_is_behind_it() => Make().IsAvailable.ShouldBeTrue();

    [Fact]
    public async Task Removes_every_object_under_this_jobs_prefix_and_keeps_siblings_cache_and_config()
    {
        Seed(JobId, 3);
        Seed("other-job", 2);
        _gcp.PutObject(Bucket, "cache/evo2/weights.pt", [9]);
        _gcp.PutObject(Bucket, "app-config.json", [7]);
        _gcp.PutObject(Bucket, "jobs/20261003-aaaab/x.bin", [4]);

        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        _gcp.ObjectKeys(Bucket).ShouldBe(["app-config.json", "cache/evo2/weights.pt", "jobs/20261003-aaaab/x.bin", "jobs/other-job/out/file0.bin", "jobs/other-job/out/file1.bin"]);
    }

    [Fact]
    public async Task A_second_delete_of_an_empty_prefix_succeeds()
    {
        Seed(JobId, 2);
        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        _gcp.ObjectKeys(Bucket).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("jobs/")]
    [InlineData("jobs//")]
    [InlineData("cache/")]
    [InlineData("cache/jobs/x/")]
    [InlineData("jobs/x")]
    [InlineData("jobs/../")]
    [InlineData("jobs/a/b/")]
    [InlineData("jobs/A B/")]
    [InlineData("/jobs/x/")]
    public async Task Refuses_anything_that_is_not_exactly_one_job_folder_before_any_request(string prefix)
    {
        Seed("x", 1);

        await Should.ThrowAsync<ArgumentException>(() => Make().DeleteJobObjectsAsync(Bucket, prefix, TestContext.Current.CancellationToken));

        _gcp.ObjectKeys(Bucket).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("my-bucket")]
    [InlineData("deg-")]
    [InlineData("deg-A/../b")]
    public async Task Refuses_a_bucket_that_is_not_one_of_ours(string bucket)
    {
        await Should.ThrowAsync<ArgumentException>(() => Make().DeleteJobObjectsAsync(bucket, Prefix, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failure_midway_reports_how_many_objects_are_left_so_the_caller_can_retry()
    {
        Seed(JobId, 5);
        _gcp.WithPrefixDeleteFailingAfter(2, CloudErrorKind.Permission);

        var ex = await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Remaining.ShouldBe(3);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_retry_after_a_partial_failure_finishes_the_job()
    {
        Seed(JobId, 5);
        _gcp.WithPrefixDeleteFailingAfter(2, CloudErrorKind.Network);
        await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        _gcp.ObjectKeys(Bucket).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delete_that_reports_success_but_leaves_objects_is_a_partial_failure_not_success()
    {
        Seed(JobId, 4);
        _gcp.WithSilentNoOpDeletes();

        var ex = await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Remaining.ShouldBe(4);
    }

    [Fact]
    public async Task A_whole_prefix_failure_with_nothing_deleted_still_reports_the_count_left()
    {
        Seed(JobId, 2);
        _gcp.WithDeleteFailure(Prefix, CloudErrorKind.Permission);

        var ex = await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Remaining.ShouldBe(2);
    }

    [Fact]
    public async Task Run_cloud_results_delete_removes_the_prefix_records_it_and_keeps_a_sibling()
    {
        Seed(JobId, 3);
        Seed("other-job", 1);
        var runs = new InMemoryRunRepository();
        var results = new RunCloudResults(_gcp, Make(), runs, () => Path.GetTempPath(), TimeProvider.System);
        var run = new RunRecord(JobId, JobPhase.Completed, DateTimeOffset.UtcNow, Bucket: Bucket, JobPrefix: Prefix);

        var outcome = await results.DeleteAsync(run, TestContext.Current.CancellationToken);

        outcome.ShouldBe(CloudResultsStatus.Done);
        _gcp.ObjectKeys(Bucket).ShouldBe(["jobs/other-job/out/file0.bin"]);
        runs.AllRecordedInOrder.Last().CloudResultsDeleted.ShouldBeTrue();
        results.IsAvailable(runs.AllRecordedInOrder.Last()).ShouldBeFalse();
    }

    [Fact]
    public async Task Run_cloud_results_delete_that_stops_midway_says_how_many_are_left_and_keeps_the_download_available()
    {
        Seed(JobId, 4);
        _gcp.WithPrefixDeleteFailingAfter(1, CloudErrorKind.Network);
        var runs = new InMemoryRunRepository();
        var results = new RunCloudResults(_gcp, Make(), runs, () => Path.GetTempPath(), TimeProvider.System);
        var run = new RunRecord(JobId, JobPhase.Completed, DateTimeOffset.UtcNow, Bucket: Bucket, JobPrefix: Prefix);

        var outcome = await results.DeleteAsync(run, TestContext.Current.CancellationToken);

        outcome.ShouldBe(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, 3));
        runs.AllRecordedInOrder.ShouldBeEmpty();
        results.CanDelete(run).ShouldBeTrue();
    }
}
