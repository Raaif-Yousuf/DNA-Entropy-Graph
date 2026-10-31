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

/// <summary>Issue #1047: a re-download after "Delete files on this PC" recreates the recorded folder with this run's marker, so a later delete does not ask the older-run question.</summary>
public sealed class RunCloudResultsRedownloadMarkerTests : IDisposable
{
    private const string Bucket = "deg-proj-fake";
    private const string JobId = "20261003-bbbb";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-runs-1047", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();
    private readonly DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] BedGraph = Encoding.UTF8.GetBytes("chr1\t0\t10\t1.5\n");

    public RunCloudResultsRedownloadMarkerTests()
    {
        _root = Path.Combine(_base, "Downloads");
        Directory.CreateDirectory(_root);
        var json = $$"""{"schema":1,"status":"done","inputs":[{"id":"in1","status":"done","files":[{"path":"output/sample.bedgraph","sha256":"{{Convert.ToHexString(SHA256.HashData(BedGraph))}}","bytes":{{BedGraph.Length}}}]}]}""";
        _gcp.PutObject(Bucket, Prefix + "result.json", Encoding.UTF8.GetBytes(json));
        _gcp.PutObject(Bucket, Prefix + "output/sample.bedgraph", BedGraph);
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

    private static string Prefix => WorkerManifestBuilder.JobPrefix(JobId);

    private RunCloudResults Results() => new(_gcp, Substitute.For<IJobObjectDeleter>(), _runs, () => _root, new FixedTime(_now));

    private LocalRunFiles Local() => new(Substitute.For<IRunInputStore>(), () => _root, []);

    private RunRecord Run(string outputDir) => new(JobId, JobPhase.Completed, _now.AddHours(-1), Bucket: Bucket, JobPrefix: Prefix, OutputDir: outputDir,
        OptionsJson: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" }));

    [Fact]
    public async Task Delete_then_download_again_gives_a_folder_with_the_runs_marker_and_the_next_delete_proceeds_without_asking()
    {
        var folder = RunOutputFolders.CreateUnique(_root, "sample", jobId: JobId);
        File.WriteAllBytes(Path.Combine(folder, "sample.bedgraph"), BedGraph);
        var run = Run(folder);
        Local().DeleteOutputFolder(run).Status.ShouldBe(LocalDeleteStatus.Deleted);
        Directory.Exists(folder).ShouldBeFalse();

        (await Results().RedownloadAsync(run, CancellationToken.None)).ShouldBe(CloudResultsStatus.Done);

        RunFolderMarker.Check(folder, JobId).ShouldBe(RunFolderMarkerState.Matches);
        File.Exists(Path.Combine(folder, "sample.bedgraph")).ShouldBeTrue();
        Local().PlanDelete(run).ShouldBe(LocalDeletePlan.Proceed);
    }

    [Fact]
    public async Task A_recorded_folder_that_is_still_there_without_a_marker_stays_unmarked_and_still_asks_first()
    {
        var folder = Path.Combine(_root, "older");
        Directory.CreateDirectory(folder);
        var run = Run(folder);

        (await Results().RedownloadAsync(run, CancellationToken.None)).ShouldBe(CloudResultsStatus.Done);

        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeFalse("a folder the app did not just make is never marked on its behalf");
        Local().PlanDelete(run).ShouldBe(LocalDeletePlan.AskFirst);
    }

    [Fact]
    public async Task A_missing_recorded_folder_outside_the_output_root_is_still_refused_and_not_created()
    {
        var outside = Path.Combine(_base, "elsewhere", "sample");

        (await Results().RedownloadAsync(Run(outside), CancellationToken.None)).ShouldBe(CloudResultsStatus.Refused);

        Directory.Exists(outside).ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(outside)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_output_root_is_not_brought_back_by_a_download_again_and_says_the_folder_is_gone()
    {
        Directory.Delete(_root);
        var folder = Path.Combine(_root, "sample");

        (await Results().RedownloadAsync(Run(folder), CancellationToken.None)).Status.ShouldBe(CloudResultsStatus.OutputFolderGone);

        Directory.Exists(_root).ShouldBeFalse("a re-lettered drive or a root the user removed is not recreated on their behalf");
    }

    [Fact]
    public async Task A_recorded_folder_nested_below_the_output_root_is_not_recreated_by_this_path()
    {
        // The app only ever makes one level (CreateUnique), so a deeper recorded folder is a damaged row: it gets what it got before #1047, which is no marker made here.
        var nested = Path.Combine(_root, "a", "b");

        await Results().RedownloadAsync(Run(nested), CancellationToken.None);

        File.Exists(Path.Combine(nested, RunFolderMarker.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_nested_recorded_folder_under_a_missing_output_root_makes_nothing_at_all()
    {
        Directory.Delete(_root);
        var nested = Path.Combine(_root, "a", "b");

        (await Results().RedownloadAsync(Run(nested), CancellationToken.None)).Status.ShouldBe(CloudResultsStatus.OutputFolderGone);

        Directory.Exists(_root).ShouldBeFalse("the root is not made by the file download's CreateDirectory either");
        Directory.Exists(nested).ShouldBeFalse();
    }

    [Fact]
    public async Task A_row_with_no_job_id_under_a_missing_output_root_makes_nothing_at_all()
    {
        Directory.Delete(_root);
        var folder = Path.Combine(_root, "sample");

        (await Results().RedownloadAsync(Run(folder) with { JobId = " " }, CancellationToken.None)).Status.ShouldNotBe(CloudResultsStatus.Done);

        Directory.Exists(_root).ShouldBeFalse();
        Directory.Exists(folder).ShouldBeFalse();
    }
    [Fact]
    public async Task A_row_with_no_recorded_folder_under_a_missing_output_root_makes_nothing_at_all()
    {
        Directory.Delete(_root);

        (await Results().RedownloadAsync(Run(null!) with { OutputDir = null }, CancellationToken.None)).Status.ShouldBe(CloudResultsStatus.OutputFolderGone);

        Directory.Exists(_root).ShouldBeFalse("CreateUnique's CreateDirectory(parent) must not bring a vanished drive's folder back");
    }

    [Fact]
    public async Task A_row_with_no_recorded_folder_under_an_existing_output_root_still_gets_a_marked_folder()
    {
        var run = Run(null!) with { OutputDir = null };

        (await Results().RedownloadAsync(run, CancellationToken.None)).Status.ShouldBe(CloudResultsStatus.Done);

        var saved = (await _runs.GetAsync(JobId, CancellationToken.None))!.OutputDir!;
        RunFolderMarker.Check(saved, JobId).ShouldBe(RunFolderMarkerState.Matches);
    }

    [Fact]
    public async Task A_recorded_folder_that_now_holds_another_runs_marker_is_refused_and_nothing_is_written_into_it()
    {
        var folder = Path.Combine(_root, "sample");
        Directory.CreateDirectory(folder);
        RunFolderMarker.Write(folder, "20261003-someone-else");

        (await Results().RedownloadAsync(Run(folder), CancellationToken.None)).Status.ShouldBe(CloudResultsStatus.OtherRunsFolderRefused);

        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).ShouldBe([RunFolderMarker.FileName], "none of this run's files went into the other run's folder");
        RunFolderMarker.Check(folder, "20261003-someone-else").ShouldBe(RunFolderMarkerState.Matches);
    }
    [Fact]
    public async Task A_dangling_junction_at_the_recorded_path_is_not_recreated_through_the_link()
    {
        // MEASURED 2026-10-31: with the EntryExists check swapped for Path.Exists this still passes, because RunOutputRoot.IsContained already refuses a link at the leaf;
        // the check in ResolveFolderAsync is a second line, and this test pins the outcome (nothing made through the link), not which line stopped it.
        var target = Path.Combine(_base, "gone-target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "sample");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.SkipUnless(process.ExitCode == 0, "mklink /J is not available here, so a junction cannot be made");
        }

        Directory.Delete(target);

        try
        {
            await Results().RedownloadAsync(Run(link), CancellationToken.None);

            Directory.Exists(target).ShouldBeFalse("nothing was made through the link");
        }
        finally
        {
            // Remove the junction itself first, so cleanup never has to walk through a dangling link.
            Directory.Delete(link);
        }
    }
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
