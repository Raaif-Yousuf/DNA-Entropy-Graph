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

/// <summary>Issue #111: runs made on other computers are found in the results bucket and become read-only history rows.</summary>
public sealed class RunImporterTests : IDisposable
{
    private const string Project = "proj";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] BedGraph = Encoding.UTF8.GetBytes("chr1\t0\t10\t1.5\n");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-import-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();
    private string _bucket = string.Empty;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<RunImporter> MakeAsync()
    {
        _bucket = await _gcp.EnsureBucketAsync(Project, TestContext.Current.CancellationToken);
        return new RunImporter(_gcp, _runs);
    }

    private static string Manifest(string jobId, string installation = "inst-other-pc", string name = "plasmid") =>
        $$$"""{"schema":1,"jobId":"{{{jobId}}}","createdAt":"2026-10-14T08:30:00Z","createdBy":{"installationId":"{{{installation}}}","appVersion":"0.1.0"},"worker":{"version":"0.1.0","image":"sha256:abc"},"inputs":[{"id":"in1","name":"{{{name}}}","path":"inputs/{{{name}}}.fa"}],"predictor":{"kind":"evo","model":"evo2_7b"}}""";

    private void PutJob(string jobId, string status = "done", string? manifest = null, bool withResult = true, string? installation = null)
    {
        var prefix = WorkerManifestBuilder.JobPrefix(jobId);
        _gcp.PutObject(_bucket, prefix + "manifest.json", Encoding.UTF8.GetBytes(manifest ?? (installation is null ? Manifest(jobId) : Manifest(jobId, installation))));
        if (!withResult)
        {
            return;
        }

        var sha = Convert.ToHexString(SHA256.HashData(BedGraph));
        var error = status == "failed" ? ",\"error\":{\"code\":\"WORKER_FAILED\",\"message\":\"boom\"}" : string.Empty;
        var files = status == "done" ? $"[{{\"path\":\"output/{jobId}.bedgraph\",\"sha256\":\"{sha}\",\"bytes\":{BedGraph.Length}}}]" : "[]";
        _gcp.PutObject(_bucket, prefix + "result.json", Encoding.UTF8.GetBytes($$"""{"schema":1,"status":"{{status}}"{{error}},"inputs":[{"id":"in1","status":"{{status}}","files":{{files}}}]}"""));
        if (status == "done")
        {
            _gcp.PutObject(_bucket, prefix + $"output/{jobId}.bedgraph", BedGraph);
        }
    }

    [Fact]
    public async Task Two_foreign_runs_become_two_imported_rows_with_where_they_came_from()
    {
        var importer = await MakeAsync();
        PutJob("20261014-aaaa");
        PutJob("20261014-bbbb", "failed", installation: "inst-third-pc");

        var result = await importer.ImportAsync(Project, TestContext.Current.CancellationToken);

        result.Imported.ShouldBe(2);
        var rows = (await _runs.GetAllAsync(TestContext.Current.CancellationToken)).OrderBy(r => r.JobId).ToList();
        rows.Select(r => r.JobId).ShouldBe(["20261014-aaaa", "20261014-bbbb"]);
        rows.ShouldAllBe(r => r.Imported && r.Bucket == _bucket && r.ProjectId == Project && r.Target == "cloud");
        rows[0].JobPrefix.ShouldBe("jobs/20261014-aaaa/");
        rows[0].Phase.ShouldBe(JobPhase.Completed);
        rows[0].InstallationId.ShouldBe("inst-other-pc");
        rows[0].Name.ShouldBe("plasmid");
        rows[0].CreatedUtc.ShouldBe(new DateTimeOffset(2026, 10, 14, 8, 30, 0, TimeSpan.Zero));
        rows[0].ManifestJson.ShouldNotBeNull();
        rows[1].Phase.ShouldBe(JobPhase.Failed);
        rows[1].ErrorCode.ShouldBe("WORKER_FAILED");
        rows[1].InstallationId.ShouldBe("inst-third-pc");
    }

    [Fact]
    public async Task A_cancelled_foreign_run_is_a_cancelled_row()
    {
        var importer = await MakeAsync();
        PutJob("20261014-cccc", "cancelled");

        await importer.ImportAsync(Project, TestContext.Current.CancellationToken);

        (await _runs.GetAllAsync(TestContext.Current.CancellationToken)).Single().Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_run_already_in_the_local_history_is_never_imported_or_changed()
    {
        var importer = await MakeAsync();
        PutJob("20261014-aaaa");
        PutJob("20261014-bbbb");
        var mine = new RunRecord("20261014-aaaa", JobPhase.Failed, Now, Name: "mine", Imported: false);
        await _runs.UpsertAsync(mine, TestContext.Current.CancellationToken);

        var result = await importer.ImportAsync(Project, TestContext.Current.CancellationToken);

        result.Imported.ShouldBe(1);
        result.AlreadyKnown.ShouldBe(1);
        (await _runs.GetAllAsync(TestContext.Current.CancellationToken)).Single(r => r.JobId == "20261014-aaaa").ShouldBe(mine);
    }

    [Fact]
    public async Task A_second_scan_imports_nothing_new()
    {
        var importer = await MakeAsync();
        PutJob("20261014-aaaa");

        await importer.ImportAsync(Project, TestContext.Current.CancellationToken);
        var again = await importer.ImportAsync(Project, TestContext.Current.CancellationToken);

        again.Imported.ShouldBe(0);
        again.AlreadyKnown.ShouldBe(1);
        (await _runs.GetAllAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_with_no_result_yet_is_skipped_not_imported_as_finished()
    {
        var importer = await MakeAsync();
        PutJob("20261014-aaaa", withResult: false);

        var result = await importer.ImportAsync(Project, TestContext.Current.CancellationToken);

        result.Imported.ShouldBe(0);
        result.Skipped.ShouldBe(1);
        (await _runs.GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreadable_manifest_or_one_naming_a_different_job_is_skipped_and_the_rest_still_import()
    {
        var importer = await MakeAsync();
        PutJob("20261014-aaaa", manifest: "{not json");
        PutJob("20261014-bbbb", manifest: Manifest("20261014-zzzz"));
        PutJob("20261014-cccc");

        var result = await importer.ImportAsync(Project, TestContext.Current.CancellationToken);

        result.Imported.ShouldBe(1);
        result.Skipped.ShouldBe(2);
        (await _runs.GetAllAsync(TestContext.Current.CancellationToken)).Single().JobId.ShouldBe("20261014-cccc");
    }

    [Fact]
    public async Task A_folder_with_no_manifest_is_skipped()
    {
        var importer = await MakeAsync();
        _gcp.PutObject(_bucket, "jobs/20261014-aaaa/log.txt", [1]);

        var result = await importer.ImportAsync(Project, TestContext.Current.CancellationToken);

        result.Skipped.ShouldBe(1);
    }

    [Fact]
    public async Task A_project_with_no_results_bucket_imports_nothing()
    {
        var result = await new RunImporter(_gcp, _runs).ImportAsync("never-set-up", TestContext.Current.CancellationToken);

        result.ShouldBe(new RunImportResult(0, 0, 0));
    }

    [Fact]
    public async Task An_imported_row_can_be_downloaded_again_but_not_deleted_in_the_cloud()
    {
        var importer = await MakeAsync();
        PutJob("20261014-aaaa");
        await importer.ImportAsync(Project, TestContext.Current.CancellationToken);
        var row = (await _runs.GetAllAsync(TestContext.Current.CancellationToken)).Single();
        var downloads = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(downloads);
        var deleter = Substitute.For<IJobObjectDeleter>();
        deleter.IsAvailable.Returns(true);
        var cloud = new RunCloudResults(_gcp, deleter, _runs, () => downloads, new FixedTime(Now));

        cloud.IsAvailable(row).ShouldBeTrue();
        cloud.CanDelete(row).ShouldBeFalse();
        (await cloud.DeleteAsync(row, TestContext.Current.CancellationToken)).ShouldBe(CloudResultsStatus.Refused);
        await deleter.DidNotReceiveWithAnyArgs().DeleteJobObjectsAsync(default!, default!, TestContext.Current.CancellationToken);

        var status = await cloud.RedownloadAsync(row, TestContext.Current.CancellationToken);

        status.Status.ShouldBe(CloudResultsStatus.Done);
        Directory.GetFiles(downloads, "*.bedgraph", SearchOption.AllDirectories).Single().ShouldEndWith("20261014-aaaa.bedgraph");
    }
}
