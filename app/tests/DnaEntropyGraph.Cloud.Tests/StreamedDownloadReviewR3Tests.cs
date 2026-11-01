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
/// Issue #588, round 3 of the cold review: one containment rule (strictly inside AND no junction or symlink) shared by both consumers and the sweeper,
/// no write through a junction below the run folder, and a count of renamed-aside files that survives every failure path.
/// </summary>
public sealed class StreamedDownloadReviewR3Tests : IDisposable
{
    private const string JobId = "20261003-rrr3";
    private const string Bucket = "deg-proj-fake";
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string TransferBucket = FakeGcp.BucketName("my-project");
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-588r3-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();
    private readonly List<string> _junctions = [];

    public StreamedDownloadReviewR3Tests()
    {
        _root = Path.Combine(_base, "Downloads");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var link in _junctions.Where(Directory.Exists))
        {
            Directory.Delete(link, recursive: false);
        }

        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Folder(string name)
    {
        var dir = Path.Combine(_base, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private bool Junction(string link, string target)
    {
        try
        {
            _junctions.Add(link);
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            process!.WaitForExit(10000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static VmSpec Spec(string jobId)
        => new("my-project", "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE");

    private static WorkerResultFile File1(byte[] content, string path) => new(path, Sha(content), content.Length);

    private static WorkerResultDocument Doc(string status, params WorkerResultFile[] files)
        => new(status, [new WorkerInputResult("in1", status, files, null, null)], null, null);

    private static string Json(string status, params (string Path, byte[] Content)[] files)
        => $$"""{"schema":1,"status":"{{status}}","inputs":[{"id":"in1","status":"{{status}}","files":[{{string.Join(",", files.Select(f => $$"""{"path":"{{f.Path}}","sha256":"{{Sha(f.Content)}}","bytes":{{f.Content.Length}}}"""))}}]}]}""";

    // ---- shared pieces for the RunCloudResults tests ----

    private static string Prefix => WorkerManifestBuilder.JobPrefix(JobId);

    private RunCloudResults MakeResults()
        => new(_gcp, Substitute.For<IJobObjectDeleter>(), _runs, () => _root, TimeProvider.System, null, null);

    private static RunRecord Run(string folder)
        => new(JobId, JobPhase.Completed, DateTimeOffset.UtcNow.AddHours(-1), Bucket: Bucket, JobPrefix: Prefix, OutputDir: folder,
            OptionsJson: RunOptionsJson.Serialize(new RunOptions { ModelId = "m", RunTarget = "cloud" }));

    private void PutResult(string path, byte[] content)
    {
        _gcp.PutObject(Bucket, Prefix + "result.json", Encoding.UTF8.GetBytes(Json("done", (path, content))));
        _gcp.PutObject(Bucket, Prefix + path, content);
    }

    // ---- 1: one containment rule, both consumers and the sweeper ----

    [Fact]
    public async Task A_redownload_whose_recorded_folder_is_a_junction_is_refused_and_nothing_is_swept_or_written_through_it()
    {
        var target = Folder("r3-target");
        var stale = Path.Combine(target, "x.bedgraph.0123456789ab.part");
        File.WriteAllBytes(stale, [1]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        var link = Path.Combine(_root, "linked");
        if (!Junction(link, target))
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        PutResult("output/x.bedgraph", [1, 2, 3]);

        var result = await MakeResults().RedownloadAsync(Run(link), CancellationToken.None);

        result.Status.ShouldBe(CloudResultsStatus.Refused);
        File.Exists(stale).ShouldBeTrue("the sweeper must not delete through a junction");
        Directory.GetFiles(target).Length.ShouldBe(1, "nothing was downloaded through the junction");
    }

    [Fact]
    public void The_sweeper_refuses_a_root_that_is_itself_a_junction()
    {
        var target = Folder("r3s-target");
        var stale = Path.Combine(target, "x.bedgraph.0123456789ab.part");
        File.WriteAllBytes(stale, [1]);
        File.SetLastWriteTimeUtc(stale, Now.AddDays(-3));
        var link = Path.Combine(_base, "r3s-link");
        if (!Junction(link, target))
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        StalePartSweeper.Sweep(link, [], Now);

        File.Exists(stale).ShouldBeTrue();
    }

    // ---- 2: a junction BELOW the run folder is never written through ----

    [Fact]
    public async Task The_runs_own_download_writes_nothing_through_a_junction_below_the_run_folder()
    {
        const string jobId = "job-r3b";
        var outside = Folder("r3b-outside");
        var request = TestInputs.Request(jobId, Spec(jobId));
        var folder = Path.Combine(request.OutputFolder, "resumed");
        Directory.CreateDirectory(folder);
        RunFolderMarker.Write(folder, jobId); // issue #1043
        if (!Junction(Path.Combine(folder, "seq"), outside))
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        var worker = "worker bytes"u8.ToArray();
        _gcp.PutObject(TransferBucket, WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/seq.bedgraph", worker);
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Downloading, DateTimeOffset.UtcNow, OutputDir: folder), CancellationToken.None);
        var settings = new CloudRunSettings();

        var ex = await Should.ThrowAsync<RunFailureException>(() => new RunTransfer(_gcp, new GatewayCalls(settings, _gcp), settings, new RunRowStore(_runs, null))
            .DownloadAsync(request, TransferBucket, Doc("done", File1(worker, "output/seq/seq.bedgraph")), CancellationToken.None));

        ex.Code.ShouldBe(RunErrorCodes.DownloadFailed);
        Directory.GetFileSystemEntries(outside).ShouldBeEmpty("no temp file, rename or move went through the junction");
    }

    [Fact]
    public async Task A_redownload_writes_nothing_through_a_junction_below_the_run_folder()
    {
        var outside = Folder("r3c-outside");
        var folder = Path.Combine(_root, "sample");
        Directory.CreateDirectory(folder);
        if (!Junction(Path.Combine(folder, "seq"), outside))
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        PutResult("output/seq/x.bedgraph", [1, 2, 3]);

        var result = await MakeResults().RedownloadAsync(Run(folder), CancellationToken.None);

        result.Status.ShouldBe(CloudResultsStatus.Failed);
        Directory.GetFileSystemEntries(outside).ShouldBeEmpty();
    }

    // ---- 3: the aside count survives every path ----

    private (RunTransfer Transfer, CloudJobRequest Request, string Folder) TransferWithEditedFile(string jobId, AfterTaskAction after, byte[] worker, byte[] edited, IRunFileMover? mover = null)
    {
        var request = TestInputs.Request(jobId, Spec(jobId), afterTask: after);
        var folder = Path.Combine(request.OutputFolder, "resumed");
        Directory.CreateDirectory(Path.Combine(folder, "seq"));
        RunFolderMarker.Write(folder, jobId); // issue #1043
        File.WriteAllBytes(Path.Combine(folder, "seq", "a.bedgraph"), edited);
        _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Downloading, DateTimeOffset.UtcNow, OutputDir: folder), CancellationToken.None).GetAwaiter().GetResult();
        _gcp.PutObject(TransferBucket, WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/a.bedgraph", worker);
        var settings = new CloudRunSettings();
        return (new RunTransfer(_gcp, new GatewayCalls(settings, _gcp), settings, new RunRowStore(_runs, null), mover), request, folder);
    }

    private RunOutcomeRecorder Recorder(RunTransfer transfer)
    {
        var settings = new CloudRunSettings();
        var calls = new GatewayCalls(settings, _gcp);
        return new RunOutcomeRecorder(new RunRowStore(_runs, null), transfer, new VmTerminator(_gcp, calls, settings));
    }

    [Fact]
    public async Task The_count_of_renamed_aside_files_survives_a_later_file_that_fails()
    {
        var worker = "worker"u8.ToArray();
        var (transfer, request, folder) = TransferWithEditedFile("job-r3d", AfterTaskAction.Stop, worker, "edited"u8.ToArray());
        var tally = new DownloadTally();

        await Should.ThrowAsync<RunFailureException>(() => transfer.DownloadAsync(
            request, TransferBucket, Doc("done", File1(worker, "output/seq/a.bedgraph"), File1([9], "output/seq/missing.bedgraph")), CancellationToken.None, tally));

        tally.KeptAside.ShouldBe(1, "the first file was renamed aside before the second failed");
        Directory.GetFiles(Path.Combine(folder, "seq"), "*(changed*").Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_run_that_kept_a_partial_download_still_records_the_renamed_aside_file()
    {
        var worker = "worker"u8.ToArray();
        var (transfer, request, _) = TransferWithEditedFile("job-r3e", AfterTaskAction.Stop, worker, "edited"u8.ToArray());

        var result = await Recorder(transfer).FinishAsync(request, TransferBucket, null, Json("failed", ("output/seq/a.bedgraph", worker)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        _runs.AllRecordedInOrder.Last(r => r.JobId == "job-r3e").ErrorDetail!.ShouldContain("changed file(s)");
        // Issue #929: the count is a mark the Runs row and the run page read, not only prose in a diagnostics-only field.
        KeptAsideDetail.TryRead(_runs.AllRecordedInOrder.Last(r => r.JobId == "job-r3e").ErrorDetail).ShouldBe(1);
    }

    [Fact]
    public async Task A_download_that_fails_after_renaming_a_file_aside_keeps_its_code_and_names_the_renamed_file()
    {
        var worker = "worker"u8.ToArray();
        var (transfer, request, _) = TransferWithEditedFile("job-r3f", AfterTaskAction.KeepAlive, worker, "edited"u8.ToArray());

        var ex = await Should.ThrowAsync<RunFailureException>(() => Recorder(transfer).FinishAsync(
            request, TransferBucket, null, Json("done", ("output/seq/a.bedgraph", worker), ("output/seq/missing.bedgraph", [9])), CancellationToken.None));

        ex.Code.ShouldBe(RunErrorCodes.WorkerFailed, "the primary error stays the headline");
        ex.Message!.ShouldContain("changed file(s)");
        KeptAsideDetail.TryRead(ex.Message).ShouldBe(1, "issue #929: the failed-download path carries the count as the mark too");
    }

    [Fact]
    public async Task A_finished_run_with_an_unconfirmed_vm_end_still_records_the_renamed_aside_file()
    {
        var worker = "worker"u8.ToArray();
        var (transfer, request, _) = TransferWithEditedFile("job-r3g", AfterTaskAction.Stop, worker, "edited"u8.ToArray());
        var settings = new CloudRunSettings { LifecycleTimeout = TimeSpan.FromMilliseconds(80), LifecyclePollInterval = TimeSpan.FromMilliseconds(1) };
        await _gcp.WithStopBehaviour(stoppingPolls: 1_000_000).CreateVmAsync(request.Spec, "us-central1-a", CancellationToken.None);
        var recorder = new RunOutcomeRecorder(new RunRowStore(_runs, null), transfer, new VmTerminator(_gcp, new GatewayCalls(settings, _gcp), settings));

        var result = await recorder.FinishAsync(request, TransferBucket, "us-central1-a", Json("done", ("output/seq/a.bedgraph", worker)), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.LifecycleUnverified, "vacuity: the stop never landed");
        result.FailureMessage!.ShouldContain("changed file(s)");
        _runs.AllRecordedInOrder.Last(r => r.JobId == "job-r3g").ErrorDetail!.ShouldContain("changed file(s)");
        KeptAsideDetail.TryRead(_runs.AllRecordedInOrder.Last(r => r.JobId == "job-r3g").ErrorDetail).ShouldBe(1, "issue #929: the lifecycle-unverified row carries the count as the mark");
    }
    private sealed class RestoreRefusingMover : IRunFileMover
    {
        private readonly DiskRunFileMover _disk = new();

        public void Move(string source, string destination)
        {
            if (source.EndsWith(".part", StringComparison.Ordinal))
            {
                throw new IOException("the final move failed");
            }

            if (!destination.Contains("(changed", StringComparison.Ordinal) && source.Contains("(changed", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("the restore was refused");
            }

            _disk.Move(source, destination);
        }
    }

    [Fact]
    public async Task A_restore_that_is_refused_does_not_replace_the_original_error_and_the_file_stays_counted()
    {
        var worker = "worker"u8.ToArray();
        var (transfer, request, folder) = TransferWithEditedFile("job-r3h", AfterTaskAction.Stop, worker, "edited"u8.ToArray(), new RestoreRefusingMover());
        var tally = new DownloadTally();

        var ex = await Should.ThrowAsync<RunFailureException>(() => transfer.DownloadAsync(request, TransferBucket, Doc("done", File1(worker, "output/seq/a.bedgraph")), CancellationToken.None, tally));

        ex.Message.ShouldContain("IOException", Case.Sensitive, "the final-move failure is the story, not the refused restore");
        tally.KeptAside.ShouldBe(1, "the file is still under its changed name");
        Directory.GetFiles(Path.Combine(folder, "seq"), "*(changed*").Length.ShouldBe(1);
    }

    // ---- Low: an abandoned download does not leave its temp file for the next day's sweep ----

    [Fact]
    public async Task A_callee_that_ignores_cancellation_has_its_temp_file_deleted_once_it_lets_go()
    {
        var path = Path.Combine(_base, "abandoned.bedgraph.0123456789ab.part");
        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadToFileAsync(Arg.Any<CloudScope>(), Arg.Any<string>(), Arg.Any<string>(), path, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await Task.Delay(900, CancellationToken.None);
            return true;
        });

        await Should.ThrowAsync<CloudOperationException>(() => StreamedDownload.RunAsync(storage, TimeProvider.System, TestScopedGateways.Default, "b", "k", path, null, TimeSpan.FromMilliseconds(200), CancellationToken.None, null, TimeSpan.FromMilliseconds(100)));

        var deadline = Stopwatch.StartNew();
        while (File.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        File.Exists(path).ShouldBeFalse("the abandoned attempt's temp file is removed after the callee finishes, not left for the next sweep");
    }
}
