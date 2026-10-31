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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}