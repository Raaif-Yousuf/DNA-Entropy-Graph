using System.Security.Cryptography;
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

/// <summary>Issue #101: re-download and delete a run's cloud results, through the real Core service over FakeGcp.</summary>
public sealed class RunCloudResultsTests : IDisposable
{
    private const string Bucket = "deg-proj-fake";
    private const string JobId = "20261003-aaaa";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-runs-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();
    private readonly IJobObjectDeleter _deleter = Substitute.For<IJobObjectDeleter>();
    private readonly DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] BedGraph = Encoding.UTF8.GetBytes("chr1\t0\t10\t1.5\n");

    public RunCloudResultsTests()
    {
        _root = Path.Combine(_base, "Downloads");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RunCloudResults Make(IStorageGateway? storage = null) => new(storage ?? _gcp, _deleter, _runs, () => _root, new FixedTime(_now));

    private static string Prefix => WorkerManifestBuilder.JobPrefix(JobId);

    private RunRecord Run(string? outputDir = null, DateTimeOffset? expires = null, bool deleted = false, string? prefix = null, string? bucket = Bucket)
        => new(JobId, JobPhase.Completed, _now.AddHours(-1), Bucket: bucket, JobPrefix: prefix ?? Prefix, OutputDir: outputDir ?? Path.Combine(_root, "sample"),
            CloudResultsExpireAt: expires, CloudResultsDeleted: deleted,
            OptionsJson: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" }));

    private void PutResult(string path = "output/sample.bedgraph", byte[]? content = null, string? sha = null, long? bytes = null, string status = "done")
    {
        content ??= BedGraph;
        sha ??= Convert.ToHexString(SHA256.HashData(content));
        bytes ??= content.Length;
        var json = $$"""{"schema":1,"status":"{{status}}","inputs":[{"id":"in1","status":"{{status}}","files":[{"path":"{{path}}","sha256":"{{sha}}","bytes":{{bytes}}}]}]}""";
        _gcp.PutObject(Bucket, Prefix + "result.json", Encoding.UTF8.GetBytes(json));
        _gcp.PutObject(Bucket, Prefix + path, content);
    }

    [Fact]
    public async Task Redownload_restores_a_deleted_local_folder_from_the_bucket()
    {
        PutResult();
        var folder = Path.Combine(_root, "sample");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "sample.bedgraph"), BedGraph);
        Directory.Delete(folder, recursive: true);

        var status = await Make().RedownloadAsync(Run(folder), CancellationToken.None);

        status.ShouldBe(CloudResultsStatus.Done);
        File.ReadAllBytes(Path.Combine(folder, "sample.bedgraph")).ShouldBe(BedGraph);
    }

    [Fact]
    public async Task Redownload_with_no_recorded_folder_creates_a_unique_one_under_the_output_root_and_records_it()
    {
        PutResult();
        var run = Run() with { OutputDir = null, Name = "my run" };

        var status = await Make().RedownloadAsync(run, CancellationToken.None);

        status.ShouldBe(CloudResultsStatus.Done);
        var recorded = _runs.AllRecordedInOrder.Last().OutputDir!;
        Path.GetDirectoryName(recorded).ShouldBe(_root);
        File.Exists(Path.Combine(recorded, "sample.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public async Task Redownload_after_the_retention_window_is_expired_and_downloads_nothing()
    {
        PutResult();
        var folder = Path.Combine(_root, "sample");

        var status = await Make().RedownloadAsync(Run(folder, expires: _now.AddMinutes(-1)), CancellationToken.None);

        status.ShouldBe(CloudResultsStatus.Expired);
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public async Task Redownload_when_the_cloud_copy_was_deleted_or_never_recorded_is_no_cloud_copy()
    {
        PutResult();
        var make = Make();

        (await make.RedownloadAsync(Run(deleted: true), CancellationToken.None)).ShouldBe(CloudResultsStatus.NoCloudCopy);
        (await make.RedownloadAsync(Run(bucket: null), CancellationToken.None)).ShouldBe(CloudResultsStatus.NoCloudCopy);
    }

    [Fact]
    public async Task Redownload_when_the_bucket_has_no_result_json_says_so()
        => (await Make().RedownloadAsync(Run(), CancellationToken.None)).ShouldBe(CloudResultsStatus.ResultNotFound);

    [Fact]
    public async Task A_file_that_fails_its_checksum_fails_the_download_and_leaves_no_final_file()
    {
        PutResult(sha: new string('0', 64));
        var folder = Path.Combine(_root, "sample");

        (await Make().RedownloadAsync(Run(folder), CancellationToken.None)).ShouldBe(CloudResultsStatus.Failed);

        File.Exists(Path.Combine(folder, "sample.bedgraph")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("output/../../escape.txt")]
    [InlineData("output/a/../../../escape.txt")]
    [InlineData("secrets.txt")]
    [InlineData("output/C:/escape.txt")]
    public async Task A_result_path_that_leaves_the_output_folder_is_refused_and_writes_nothing_outside(string path)
    {
        PutResult(path: path);
        var folder = Path.Combine(_root, "sample");

        (await Make().RedownloadAsync(Run(folder), CancellationToken.None)).ShouldBe(CloudResultsStatus.Failed);

        File.Exists(Path.Combine(_base, "escape.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "escape.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_recorded_folder_outside_the_output_root_is_refused_before_anything_is_written()
    {
        PutResult();
        var outside = Path.Combine(_base, "Documents", "sample");

        (await Make().RedownloadAsync(Run(outside), CancellationToken.None)).ShouldBe(CloudResultsStatus.Refused);

        Directory.Exists(outside).ShouldBeFalse();
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task A_job_that_did_not_finish_restores_its_partial_files_but_says_partial_not_done(string status)
    {
        PutResult(status: status);
        var folder = Path.Combine(_root, "sample");

        (await Make().RedownloadAsync(Run(folder), CancellationToken.None)).ShouldBe(CloudResultsStatus.Partial);

        File.Exists(Path.Combine(folder, "sample.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_file_already_in_the_folder_is_never_overwritten_and_missing_ones_are_restored()
    {
        PutResult();
        var folder = Path.Combine(_root, "sample");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "sample.bedgraph"), "my own edits");

        (await Make().RedownloadAsync(Run(folder), CancellationToken.None)).ShouldBe(CloudResultsStatus.Done);

        File.ReadAllText(Path.Combine(folder, "sample.bedgraph")).ShouldBe("my own edits");
        File.Delete(Path.Combine(folder, "sample.bedgraph"));
        (await Make().RedownloadAsync(Run(folder), CancellationToken.None)).ShouldBe(CloudResultsStatus.Done);
        File.ReadAllBytes(Path.Combine(folder, "sample.bedgraph")).ShouldBe(BedGraph);
    }

    [Fact]
    public async Task A_recorded_folder_with_a_trailing_separator_still_downloads()
    {
        PutResult();
        var folder = Path.Combine(_root, "sample") + Path.DirectorySeparatorChar;

        (await Make().RedownloadAsync(Run(folder), CancellationToken.None)).ShouldBe(CloudResultsStatus.Done);

        File.Exists(Path.Combine(_root, "sample", "sample.bedgraph")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Any_unexpected_storage_failure_is_failed_not_thrown(Type exception)
    {
        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs<Task<Stream?>>(_ => throw (Exception)Activator.CreateInstance(exception)!);

        (await Make(storage).RedownloadAsync(Run(), CancellationToken.None)).ShouldBe(CloudResultsStatus.Failed);
    }

    [Fact]
    public void Can_delete_needs_an_available_cloud_copy_and_a_connected_deleter()
    {
        var make = Make();
        _deleter.IsAvailable.Returns(true);
        make.CanDelete(Run()).ShouldBeTrue();
        make.CanDelete(Run(deleted: true)).ShouldBeFalse();
        _deleter.IsAvailable.Returns(false);
        make.CanDelete(Run()).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_removes_only_this_jobs_prefix_and_records_the_deletion()
    {
        var run = Run();

        var status = await Make().DeleteAsync(run, CancellationToken.None);

        status.ShouldBe(CloudResultsStatus.Done);
        await _deleter.Received(1).DeleteJobObjectsAsync(Bucket, Prefix, Arg.Any<CancellationToken>());
        _runs.AllRecordedInOrder.Last().CloudResultsDeleted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("jobs/")]
    [InlineData("jobs//")]
    [InlineData("jobs/other-job/")]
    [InlineData("jobs/20261003-aaaa")]
    [InlineData("jobs/20261003-aaaa/../other/")]
    public async Task Delete_refuses_a_prefix_that_is_not_exactly_this_jobs_prefix(string prefix)
    {
        (await Make().DeleteAsync(Run(prefix: prefix), CancellationToken.None)).ShouldBe(CloudResultsStatus.Refused);

        await _deleter.DidNotReceiveWithAnyArgs().DeleteJobObjectsAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delete_of_an_already_deleted_copy_does_not_call_the_bucket_again()
    {
        (await Make().DeleteAsync(Run(deleted: true), CancellationToken.None)).ShouldBe(CloudResultsStatus.NoCloudCopy);

        await _deleter.DidNotReceiveWithAnyArgs().DeleteJobObjectsAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_failing_delete_is_failed_and_the_row_is_not_marked_deleted()
    {
        _deleter.DeleteJobObjectsAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs<Task>(_ => throw new InvalidOperationException("boom"));

        (await Make().DeleteAsync(Run(), CancellationToken.None)).ShouldBe(CloudResultsStatus.Failed);

        _runs.AllRecordedInOrder.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deleter_that_is_not_connected_reports_not_connected_and_keeps_the_row()
    {
        _deleter.DeleteJobObjectsAsync(default!, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs<Task>(_ => throw new CloudNotConnectedException());

        (await Make().DeleteAsync(Run(), CancellationToken.None)).ShouldBe(CloudResultsStatus.NotConnected);

        _runs.AllRecordedInOrder.ShouldBeEmpty();
    }

    [Fact]
    public void Is_available_reflects_expiry_deletion_and_a_recorded_location()
    {
        var make = Make();
        make.IsAvailable(Run()).ShouldBeTrue();
        make.IsAvailable(Run(expires: _now.AddDays(1))).ShouldBeTrue();
        make.IsAvailable(Run(expires: _now.AddSeconds(-1))).ShouldBeFalse();
        make.IsAvailable(Run(deleted: true)).ShouldBeFalse();
        make.IsAvailable(Run(bucket: null)).ShouldBeFalse();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
