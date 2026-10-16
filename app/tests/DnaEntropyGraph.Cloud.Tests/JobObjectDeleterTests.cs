using System.Text;
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
    private const string Installation = "inst-1";
    private static readonly string Prefix = WorkerManifestBuilder.JobPrefix(JobId);
    private readonly FakeGcp _gcp = new();

    private JobObjectDeleter Make(string? installation = Installation) => new(_gcp, () => installation);

    /// <summary>A job folder: <paramref name="count"/> output files plus its manifest (so count + 1 objects), written by <paramref name="owner"/>.</summary>
    private void Seed(string jobId, int count, string owner = Installation)
    {
        for (var i = 0; i < count; i++)
        {
            _gcp.PutObject(Bucket, $"jobs/{jobId}/out/file{i}.bin", [1, 2, 3]);
        }

        _gcp.PutObject(Bucket, $"jobs/{jobId}/manifest.json", Encoding.UTF8.GetBytes("{\"createdBy\":{\"installationId\":\"" + owner + "\"}}"));
    }

    private RunCloudResults Results(out InMemoryRunRepository runs)
    {
        runs = new InMemoryRunRepository();
        return new RunCloudResults(_gcp, Make(), runs, () => Path.GetTempPath(), TimeProvider.System);
    }

    private static RunRecord Run() => new(JobId, JobPhase.Completed, DateTimeOffset.UtcNow, Bucket: Bucket, JobPrefix: Prefix);

    [Fact]
    public void Is_available_because_a_real_gateway_is_behind_it() => Make().IsAvailable.ShouldBeTrue();

    [Fact]
    public async Task Removes_every_object_under_this_jobs_prefix_and_keeps_siblings_cache_and_config()
    {
        Seed(JobId, 3);
        Seed("other-job", 1);
        _gcp.PutObject(Bucket, "cache/evo2/weights.pt", [9]);
        _gcp.PutObject(Bucket, "app-config.json", [7]);
        _gcp.PutObject(Bucket, "jobs/20261003-aaaab/x.bin", [4]);

        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        _gcp.ObjectKeys(Bucket).ShouldBe(["app-config.json", "cache/evo2/weights.pt", "jobs/20261003-aaaab/x.bin", "jobs/other-job/manifest.json", "jobs/other-job/out/file0.bin"]);
    }

    [Fact]
    public async Task A_second_delete_of_an_empty_prefix_succeeds_even_with_no_manifest_left()
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
    [InlineData("jobs/x/\n")]
    public async Task Refuses_anything_that_is_not_exactly_one_job_folder_before_any_request(string prefix)
    {
        Seed("x", 1);

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make().DeleteJobObjectsAsync(Bucket, prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.NotAJobFolder);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("my-bucket")]
    [InlineData("deg-")]
    [InlineData("deg-A/../b")]
    public async Task Refuses_a_bucket_that_is_not_one_of_ours(string bucket)
    {
        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make().DeleteJobObjectsAsync(bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.NotAJobFolder);
    }

    [Fact]
    public async Task A_job_folder_another_installation_made_is_refused_and_left_untouched()
    {
        Seed(JobId, 3, owner: "someone-else");

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.OtherComputer);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_folder_with_objects_but_no_readable_manifest_is_refused_and_left_untouched()
    {
        _gcp.PutObject(Bucket, Prefix + "out/a.bin", [1]);
        _gcp.PutObject(Bucket, Prefix + "manifest.json", Encoding.UTF8.GetBytes("not json"));

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.OtherComputer);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_installation_id_refuses_rather_than_guess()
    {
        Seed(JobId, 1);

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make(installation: null).DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.OtherComputer);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_failure_midway_reports_how_many_objects_are_left_so_the_caller_can_retry()
    {
        Seed(JobId, 4);
        _gcp.WithPrefixDeleteFailingAfter(2, CloudErrorKind.Permission);

        var ex = await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Remaining.ShouldBe(3);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_retry_after_a_partial_failure_finishes_the_job()
    {
        Seed(JobId, 4);
        _gcp.WithPrefixDeleteFailingAfter(2, CloudErrorKind.Network);
        await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        _gcp.ObjectKeys(Bucket).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sweep_that_threw_after_deleting_everything_is_success_after_a_clean_read_back()
    {
        Seed(JobId, 2);
        _gcp.AfterPrefixDelete(_ => throw new IOException("the connection dropped after the last delete"));

        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        _gcp.ObjectKeys(Bucket).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delete_that_reports_success_but_removed_nothing_is_a_failure_not_success()
    {
        Seed(JobId, 3);
        _gcp.WithSilentNoOpDeletes();

        await Should.ThrowAsync<InvalidOperationException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_whole_prefix_failure_with_nothing_deleted_surfaces_the_classified_failure_not_a_partial()
    {
        Seed(JobId, 2);
        _gcp.WithDeleteFailure(Prefix, CloudErrorKind.Permission);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Run_cloud_results_delete_removes_the_prefix_records_it_and_keeps_a_sibling()
    {
        Seed(JobId, 3);
        Seed("other-job", 1);
        var results = Results(out var runs);

        var outcome = await results.DeleteAsync(Run(), TestContext.Current.CancellationToken);

        outcome.ShouldBe(CloudResultsStatus.Done);
        _gcp.ObjectKeys(Bucket).ShouldBe(["jobs/other-job/manifest.json", "jobs/other-job/out/file0.bin"]);
        runs.AllRecordedInOrder.Last().CloudResultsDeleted.ShouldBeTrue();
        results.IsAvailable(runs.AllRecordedInOrder.Last()).ShouldBeFalse();
    }

    [Fact]
    public async Task Run_cloud_results_delete_that_stops_midway_says_how_many_are_left_and_keeps_the_delete_available()
    {
        Seed(JobId, 4);
        _gcp.WithPrefixDeleteFailingAfter(1, CloudErrorKind.Network);
        var results = Results(out var runs);

        var outcome = await results.DeleteAsync(Run(), TestContext.Current.CancellationToken);

        outcome.ShouldBe(new CloudDeleteResult(CloudResultsStatus.PartiallyDeleted, 4));
        runs.AllRecordedInOrder.ShouldBeEmpty();
        results.CanDelete(Run()).ShouldBeTrue();
    }

    [Fact]
    public async Task Run_cloud_results_delete_with_nothing_deleted_is_the_classified_failure()
    {
        Seed(JobId, 2);
        _gcp.WithDeleteFailure(Prefix, CloudErrorKind.Permission);
        var results = Results(out var runs);

        var outcome = await results.DeleteAsync(Run(), TestContext.Current.CancellationToken);

        outcome.ShouldBe(new CloudDeleteResult(CloudResultsStatus.Failed, null, CloudErrorKind.Permission));
        runs.AllRecordedInOrder.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_cloud_results_delete_of_another_computers_run_is_other_computer_and_deletes_nothing()
    {
        Seed(JobId, 2, owner: "someone-else");
        var results = Results(out var runs);

        var outcome = await results.DeleteAsync(Run(), TestContext.Current.CancellationToken);

        outcome.ShouldBe(CloudResultsStatus.OtherComputer);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(3);
        runs.AllRecordedInOrder.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_cloud_results_delete_of_a_malformed_stored_prefix_is_refused()
    {
        Seed("x", 1);
        var results = Results(out _);

        var outcome = await results.DeleteAsync(Run() with { JobPrefix = "jobs/" }, TestContext.Current.CancellationToken);

        outcome.ShouldBe(CloudResultsStatus.Refused);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(2);
    }
}
