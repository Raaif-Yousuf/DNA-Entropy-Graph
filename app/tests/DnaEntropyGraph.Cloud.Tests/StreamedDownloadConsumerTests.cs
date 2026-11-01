using System.Diagnostics;
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

/// <summary>
/// Issue #588 (cold review): the two consumers of the streaming download (the History re-download and the run's own download) under a stall
/// deadline, with a unique temp file per attempt, never overwriting a user's file, and sweeping stale temp files.
/// </summary>
public sealed class StreamedDownloadConsumerTests : IDisposable
{
    private const string Bucket = "deg-proj-fake";
    private const string JobId = "20261003-bbbb";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-588c-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();
    private readonly DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public StreamedDownloadConsumerTests()
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

    private static string Prefix => WorkerManifestBuilder.JobPrefix(JobId);

    private RunCloudResults Make(IStorageGateway storage, TimeSpan? stall = null)
        => new(storage, Substitute.For<IJobObjectDeleter>(), _runs, () => _root, TimeProvider.System, null, stall);

    private RunRecord Run(string folder)
        => new(JobId, JobPhase.Completed, _now.AddHours(-1), Bucket: Bucket, JobPrefix: Prefix, OutputDir: folder,
            OptionsJson: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" }));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private void PutResult(byte[] content, string path = "output/sample.bedgraph", long? bytes = null)
    {
        var json = $$"""{"schema":1,"status":"done","inputs":[{"id":"in1","status":"done","files":[{"path":"{{path}}","sha256":"{{Sha(content)}}","bytes":{{bytes ?? content.Length}}}]}]}""";
        _gcp.PutObject(Bucket, Prefix + "result.json", Encoding.UTF8.GetBytes(json));
        _gcp.PutObject(Bucket, Prefix + path, content);
    }

    /// <summary>A storage whose result.json comes from the fake bucket and whose result file goes through <paramref name="download"/>.</summary>
    private IStorageGateway Storage(Func<string, CancellationToken, Task<bool>> download)
    {
        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadAsync(Arg.Any<CloudScope>(), Bucket, Prefix + "result.json", Arg.Any<CancellationToken>()).Returns(ci => _gcp.TryDownloadAsync(Bucket, Prefix + "result.json", ci.Arg<CancellationToken>()));
        storage.TryDownloadToFileAsync(Arg.Any<CloudScope>(), Bucket, Prefix + "output/sample.bedgraph", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => download(ci.ArgAt<string>(3), ci.ArgAt<CancellationToken>(4)));
        return storage;
    }

    // ---- H1: a silent connected socket cannot hang the re-download ----

    [Fact]
    public async Task A_download_that_goes_silent_is_cut_by_the_stall_deadline_the_callee_is_awaited_and_no_partial_is_left()
    {
        PutResult(new byte[100]);
        var folder = Path.Combine(_root, "sample");
        var calleeFinished = false;
        var storage = Storage(async (path, token) =>
        {
            await File.WriteAllBytesAsync(path, new byte[10], CancellationToken.None);
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(150, CancellationToken.None);
                calleeFinished = true;
                throw;
            }

            return true;
        });
        var clock = Stopwatch.StartNew();

        var status = await Make(storage, stall: TimeSpan.FromMilliseconds(300)).RedownloadAsync(Run(folder), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        status.Status.ShouldBe(CloudResultsStatus.Failed);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "a silent socket must not hang the re-download");
        calleeFinished.ShouldBeTrue("the cancelled callee is awaited before the partial is deleted");
        Directory.GetFiles(folder, "*.part").ShouldBeEmpty();
    }

    // ---- H2: slow but live is never cut ----

    [Fact]
    public async Task A_slow_download_that_keeps_moving_is_not_cut_by_the_stall_deadline()
    {
        var content = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        PutResult(content);
        var folder = Path.Combine(_root, "sample");
        var storage = Storage(async (path, token) =>
        {
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1, useAsync: true);
            foreach (var b in content)
            {
                await target.WriteAsync(new[] { b }, token);
                await target.FlushAsync(token);
                await Task.Delay(40, token);
            }

            return true;
        });

        var status = await Make(storage, stall: TimeSpan.FromMilliseconds(400)).RedownloadAsync(Run(folder), CancellationToken.None);

        status.Status.ShouldBe(CloudResultsStatus.Done);
        File.ReadAllBytes(Path.Combine(folder, "sample.bedgraph")).ShouldBe(content);
    }

    [Fact]
    public void The_total_cap_grows_with_the_file_size_and_has_a_floor()
    {
        StreamedDownload.TotalCap(1000).ShouldBe(TimeSpan.FromMinutes(5));
        StreamedDownload.TotalCap(500L * 1024 * 1024).ShouldBeGreaterThan(TimeSpan.FromMinutes(30), "500 MB at 2 Mbit/s must fit");
        StreamedDownload.TotalCap(null).ShouldBe(TimeSpan.FromMinutes(30));
    }

    // ---- M1: unique temp name per attempt ----

    [Fact]
    public async Task Each_attempt_downloads_into_its_own_unique_part_file()
    {
        var content = new byte[] { 1, 2, 3 };
        PutResult(content);
        var folder = Path.Combine(_root, "sample");
        var paths = new List<string>();
        var storage = Storage((path, _) =>
        {
            paths.Add(path);
            File.WriteAllBytes(path, content);
            return Task.FromResult(true);
        });
        var make = Make(storage);

        await make.RedownloadAsync(Run(folder), CancellationToken.None);
        File.Delete(Path.Combine(folder, "sample.bedgraph"));
        await make.RedownloadAsync(Run(folder), CancellationToken.None);

        paths.Count.ShouldBe(2);
        paths[0].ShouldNotBe(paths[1], "two downloads of one file must never write the same temp file");
        paths.ShouldAllBe(p => p.EndsWith(".part", StringComparison.Ordinal) && p != Path.Combine(folder, "sample.bedgraph.part"));
    }

    // ---- M2: the run's own download never overwrites a user's file ----

    private (RunTransfer Transfer, CloudJobRequest Request, string Folder) MakeTransfer(string jobId)
    {
        var request = TestInputs.Request(jobId, new VmSpec(
            ProjectId: "my-project", InstallationId: "install-1", JobId: jobId, Model: "evo2_7b", AppVersion: "0.1.0", Lifecycle: "stop",
            MachineType: "g2-standard-8", MaxRunDuration: TimeSpan.FromHours(4), TerminationAction: "DELETE"));
        var folder = Path.Combine(request.OutputFolder, "resumed");
        Directory.CreateDirectory(folder);
        RunFolderMarker.Write(folder, jobId); // issue #1043: a resumed run only reuses a folder that carries its own marker
        var settings = new CloudRunSettings();
        var rows = new RunRowStore(_runs, null);
        _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Downloading, _now, OutputDir: folder), CancellationToken.None).GetAwaiter().GetResult();
        return (new RunTransfer(_gcp, new GatewayCalls(settings, _gcp), settings, rows), request, folder);
    }

    private static WorkerResultDocument Doc(byte[] content, string path)
        => new("done", [new WorkerInputResult("in1", "done", [new WorkerResultFile(path, Sha(content), content.Length)], null, null)], null, null);

    [Fact]
    public async Task A_resumed_run_renames_a_user_edited_file_aside_instead_of_overwriting_it()
    {
        const string jobId = "job-m2";
        var worker = Encoding.UTF8.GetBytes("worker bytes");
        var edited = Encoding.UTF8.GetBytes("the user edited this");
        var (transfer, request, folder) = MakeTransfer(jobId);
        _gcp.PutObject(FakeGcp.BucketName("my-project"), WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/seq.bedgraph", worker);
        var destination = Path.Combine(folder, "seq", "seq.bedgraph");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, edited);

        await transfer.DownloadAsync(request, FakeGcp.BucketName("my-project"), Doc(worker, "output/seq/seq.bedgraph"), CancellationToken.None);

        File.ReadAllBytes(destination).ShouldBe(worker);
        var aside = Directory.GetFiles(Path.GetDirectoryName(destination)!, "*(changed*");
        aside.Length.ShouldBe(1, "the user's edit is kept under a changed-name");
        File.ReadAllBytes(aside[0]).ShouldBe(edited);
    }

    [Fact]
    public async Task A_resumed_run_with_identical_bytes_on_disk_creates_no_aside_copy()
    {
        const string jobId = "job-m2b";
        var worker = Encoding.UTF8.GetBytes("worker bytes");
        var (transfer, request, folder) = MakeTransfer(jobId);
        _gcp.PutObject(FakeGcp.BucketName("my-project"), WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/seq.bedgraph", worker);
        var destination = Path.Combine(folder, "seq", "seq.bedgraph");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, worker);

        await transfer.DownloadAsync(request, FakeGcp.BucketName("my-project"), Doc(worker, "output/seq/seq.bedgraph"), CancellationToken.None);

        File.ReadAllBytes(destination).ShouldBe(worker);
        Directory.GetFiles(Path.GetDirectoryName(destination)!).Length.ShouldBe(1);
    }

    [Fact]
    public async Task The_runs_own_download_cuts_a_silent_socket_too()
    {
        const string jobId = "job-h1b";
        var (_, request, folder) = MakeTransfer(jobId);
        var settings = new CloudRunSettings { DownloadStallTimeout = TimeSpan.FromMilliseconds(300) };
        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadToFileAsync(Arg.Any<CloudScope>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await File.WriteAllBytesAsync(ci.ArgAt<string>(3), new byte[5], CancellationToken.None);
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(4));
                return true;
            });
        var transfer = new RunTransfer(storage, new GatewayCalls(settings, storage), settings, new RunRowStore(_runs, null));
        var content = new byte[20];

        var ex = await Should.ThrowAsync<RunFailureException>(() => transfer.DownloadAsync(request, "b", Doc(content, "output/seq/seq.bedgraph"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(RunErrorCodes.DownloadFailed);
        Directory.GetFiles(folder, "*.part", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    // ---- M3: a transient lock on the move is retried ----

    [Fact]
    public async Task A_move_blocked_by_a_briefly_held_file_succeeds_once_it_is_released()
    {
        var source = Path.Combine(_root, "a.part");
        var destination = Path.Combine(_root, "a.bin");
        File.WriteAllBytes(source, [1]);
        var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Run(async () =>
        {
            await Task.Delay(120, TestContext.Current.CancellationToken);
            await held.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        new DiskRunFileMover().Move(source, destination);

        File.Exists(destination).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public void A_move_onto_an_existing_file_fails_at_once_and_leaves_it_alone()
    {
        var source = Path.Combine(_root, "b.part");
        var destination = Path.Combine(_root, "b.bin");
        File.WriteAllBytes(source, [1]);
        File.WriteAllBytes(destination, [9]);

        Should.Throw<IOException>(() => new DiskRunFileMover().Move(source, destination));

        File.ReadAllBytes(destination).ShouldBe(new byte[] { 9 });
    }

    // ---- L1: stale temp files are swept ----

    [Fact]
    public async Task A_stale_part_file_in_the_run_folder_is_swept_before_a_download_and_a_fresh_one_is_kept()
    {
        var content = new byte[] { 7 };
        PutResult(content);
        var folder = Path.Combine(_root, "sample");
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "sample.bedgraph.0123456789ab.part");
        var fresh = Path.Combine(folder, "sample.bedgraph.abcdef012345.part");
        File.WriteAllBytes(stale, [1]);
        File.WriteAllBytes(fresh, [1]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        var storage = Storage((path, _) =>
        {
            File.WriteAllBytes(path, content);
            return Task.FromResult(true);
        });

        await Make(storage).RedownloadAsync(Run(folder), CancellationToken.None);

        File.Exists(stale).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue("a part file younger than a day may belong to a download in progress");
    }
}
