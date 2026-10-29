using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;
using NSubstitute;
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
    public async Task A_failed_sweep_whose_read_back_also_fails_surfaces_the_classified_sweep_failure_not_a_partial_delete_of_unknown_size()
    {
        // Issue #262 review M2: the read-back swallows its own failure as "unknown"; that must not hide the real, classified error.
        var storage = NSubstitute.Substitute.For<IStorageGateway>();
        storage.GetPrefixUsageAsync(Bucket, Prefix, Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(new PrefixUsage(3, 30)),
            Task.FromException<PrefixUsage>(new IOException("read-back failed")));
        storage.TryDownloadAsync(Bucket, Prefix + "manifest.json", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("{\"createdBy\":{\"installationId\":\"" + Installation + "\"}}"))));
        storage.DeletePrefixAsync(Bucket, Prefix, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new CloudOperationException(new CloudError("FORBIDDEN", 403, "scripted"), CloudErrorKind.Permission)));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => new JobObjectDeleter(storage, () => Installation).DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task A_sweep_that_did_not_fail_but_whose_read_back_does_is_still_a_partial_delete_of_unknown_size()
    {
        // No failure to surface: the delete was accepted, the count after it is unknown, so it is not reported as done.
        var storage = NSubstitute.Substitute.For<IStorageGateway>();
        storage.GetPrefixUsageAsync(Bucket, Prefix, Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(new PrefixUsage(3, 30)),
            Task.FromException<PrefixUsage>(new IOException("read-back failed")));
        storage.TryDownloadAsync(Bucket, Prefix + "manifest.json", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("{\"createdBy\":{\"installationId\":\"" + Installation + "\"}}"))));
        storage.DeletePrefixAsync(Bucket, Prefix, Arg.Any<CancellationToken>()).Returns(Task.FromResult(3));

        var ex = await Should.ThrowAsync<PartialCloudDeleteException>(() => new JobObjectDeleter(storage, () => Installation).DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Remaining.ShouldBeNull();
    }

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

        ex.Reason.ShouldBe(CloudDeleteRefusal.Unconfirmed);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"createdBy\":\"inst-1\"}")]
    [InlineData("{\"createdBy\":{\"installationId\":7}}")]
    [InlineData("{\"createdBy\":{}}")]
    [InlineData("{}")]
    public async Task A_manifest_of_the_wrong_shape_is_not_confirmed_and_left_untouched(string manifest)
    {
        _gcp.PutObject(Bucket, Prefix + "out/a.bin", [1]);
        _gcp.PutObject(Bucket, Prefix + "manifest.json", Encoding.UTF8.GetBytes(manifest));

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.Unconfirmed);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_folder_with_objects_and_no_manifest_at_all_is_not_confirmed()
    {
        _gcp.PutObject(Bucket, Prefix + "out/a.bin", [1]);

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.Unconfirmed);
    }

    [Fact]
    public async Task An_unknown_installation_id_is_not_confirmed_rather_than_guessed()
    {
        Seed(JobId, 1);

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make(installation: null).DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.Unconfirmed);
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_manifest_is_deleted_last_so_a_sweep_that_dies_early_can_be_retried_and_finished()
    {
        // The real layout: GCS lists lexicographically, so manifest.json sorts before output/, result.json and status.json.
        foreach (var name in new[] { "control/cancel", "input/seq.fa", "logs/run.log", "output/s/s.bedgraph", "progress.jsonl", "result.json", "status.json" })
        {
            _gcp.PutObject(Bucket, Prefix + name, [1]);
        }

        _gcp.PutObject(Bucket, Prefix + "manifest.json", Encoding.UTF8.GetBytes("{\"createdBy\":{\"installationId\":\"" + Installation + "\"}}"));
        // Four objects go before it dies: in plain lexicographic order the fourth would be manifest.json, stranding the rest.
        _gcp.WithPrefixDeleteFailingAfter(4, CloudErrorKind.Network);

        var ex = await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Remaining.ShouldBe(4);
        _gcp.ObjectKeys(Bucket).ShouldContain(Prefix + "manifest.json");

        await Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken);

        _gcp.ObjectKeys(Bucket).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bucket_that_does_not_exist_is_storage_not_found_not_an_empty_success()
    {
        _gcp.WithMissingBucket(Bucket);

        var ex = await Should.ThrowAsync<CloudDeleteRefusedException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Reason.ShouldBe(CloudDeleteRefusal.StorageNotFound);
    }

    [Fact]
    public async Task More_objects_than_before_after_the_sweep_is_a_partial_with_the_count_now()
    {
        Seed(JobId, 1);
        _gcp.AfterPrefixDelete(p =>
        {
            for (var i = 0; i < 5; i++)
            {
                _gcp.PutObject(Bucket, p + $"late{i}.bin", [1]);
            }
        });

        var ex = await Should.ThrowAsync<PartialCloudDeleteException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Remaining.ShouldBe(5);
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

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Make().DeleteJobObjectsAsync(Bucket, Prefix, TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(CloudErrorKind.Other);
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
    public async Task Run_cloud_results_delete_without_a_manifest_is_not_confirmed_and_keeps_the_row()
    {
        _gcp.PutObject(Bucket, Prefix + "out/a.bin", [1]);
        var results = Results(out var runs);

        (await results.DeleteAsync(Run(), TestContext.Current.CancellationToken)).ShouldBe(CloudResultsStatus.NotConfirmed);

        runs.AllRecordedInOrder.ShouldBeEmpty();
        _gcp.ObjectKeys(Bucket).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_cloud_results_delete_in_a_missing_bucket_is_storage_not_found_and_does_not_mark_the_row()
    {
        _gcp.WithMissingBucket(Bucket);
        var results = Results(out var runs);

        (await results.DeleteAsync(Run(), TestContext.Current.CancellationToken)).ShouldBe(CloudResultsStatus.StorageNotFound);

        runs.AllRecordedInOrder.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_cloud_results_delete_of_an_already_empty_prefix_in_a_real_bucket_marks_the_row_deleted()
    {
        var results = Results(out var runs);

        (await results.DeleteAsync(Run(), TestContext.Current.CancellationToken)).ShouldBe(CloudResultsStatus.Done);

        runs.AllRecordedInOrder.Last().CloudResultsDeleted.ShouldBeTrue();
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
