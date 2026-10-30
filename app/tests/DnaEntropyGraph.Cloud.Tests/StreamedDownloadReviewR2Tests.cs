using System.Diagnostics;
using System.Security.Cryptography;
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
/// Issue #588, round 2 of the cold review: the temp-file sweeper touches only the app's own names (Hard Rule 14), the run's own download is
/// contained and puts the user's file back, a probe error is not a stall, and the aside rename is bounded.
/// </summary>
public sealed class StreamedDownloadReviewR2Tests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-588r2-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Bkt = FakeGcp.BucketName("my-project");

    public StreamedDownloadReviewR2Tests() => Directory.CreateDirectory(_base);

    private readonly List<string> _junctions = [];

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

    private static string Put(string dir, string name, int daysOld = 2)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-daysOld));
        return path;
    }

    private const string Own = "seq.bedgraph.0123456789ab.part";

    // ---- H1: the sweeper deletes only the app's own temp names ----

    [Fact]
    public void The_sweeper_deletes_a_stale_app_named_part_file_and_keeps_a_fresh_one()
    {
        var folder = Folder("h1a");
        var stale = Put(folder, Own);
        var fresh = Put(folder, "other.bedgraph.abcdef012345.part", daysOld: 0);

        StalePartSweeper.Sweep(folder, [], Now);

        File.Exists(stale).ShouldBeFalse("vacuity: the sweeper does delete the app's own stale temp file");
        File.Exists(fresh).ShouldBeTrue();
    }

    [Theory]
    [InlineData("x.part")]
    [InlineData("thesis.part")]
    [InlineData("seq.bedgraph.deadbeef.part")]
    [InlineData("seq.bedgraph.0123456789AB.part")]
    [InlineData("seq.bedgraph.0123456789abc.part")]
    [InlineData("0123456789ab.part")]
    [InlineData("seq.bedgraph.0123456789ab.part.bak")]
    public void A_foreign_part_file_older_than_a_day_survives_the_sweep(string name)
    {
        var folder = Folder("h1b");
        var foreign = Put(folder, name);

        StalePartSweeper.Sweep(folder, [], Now);

        File.Exists(foreign).ShouldBeTrue("Hard Rule 14: the user's files are read-only to us");
    }

    [Fact]
    public void A_subfolder_the_app_did_not_create_is_never_entered_but_a_listed_result_folder_is_swept()
    {
        var folder = Folder("h1c");
        var theirs = Put(Path.Combine(folder, "their-notes"), Own);
        var ours = Put(Path.Combine(folder, "seq"), Own);

        StalePartSweeper.Sweep(folder, ["seq"], Now);

        File.Exists(theirs).ShouldBeTrue("a .part in a folder no result file lives in is not ours");
        File.Exists(ours).ShouldBeFalse("a result folder's own stale temp file is swept");
    }

    [Fact]
    public void A_listed_folder_that_climbs_out_of_the_run_folder_is_ignored()
    {
        var folder = Folder("h1d");
        var outside = Put(Folder("h1d-outside"), Own);

        StalePartSweeper.Sweep(folder, [Path.Combine("..", "h1d-outside")], Now);

        File.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public void A_junction_inside_the_run_folder_is_not_followed_and_its_target_survives()
    {
        var folder = Folder("h1e");
        var target = Put(Folder("h1e-target"), Own);
        if (!TryMakeJunction(Path.Combine(folder, "seq"), Path.GetDirectoryName(target)!))
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        StalePartSweeper.Sweep(folder, ["seq"], Now);

        File.Exists(target).ShouldBeTrue("a reparse point is never followed");
    }

    private bool TryMakeJunction(string link, string target)
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

    // ---- M1: the run's own download is contained, like RunCloudResults ----

    private static VmSpec Spec(string jobId)
        => new("my-project", "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE");

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static WorkerResultDocument Doc(byte[] content, string path)
        => new("done", [new WorkerInputResult("in1", "done", [new WorkerResultFile(path, Sha(content), content.Length)], null, null)], null, null);

    private RunTransfer TransferFor(IRunFileMover? mover = null)
    {
        var settings = new CloudRunSettings();
        return new RunTransfer(_gcp, new GatewayCalls(settings, _gcp), settings, new RunRowStore(_runs, null), mover);
    }

    [Fact]
    public async Task A_recorded_output_folder_outside_the_runs_output_folder_is_not_written_to()
    {
        const string jobId = "job-r2m1";
        var elsewhere = Folder("m1-elsewhere");
        var worker = "worker bytes"u8.ToArray();
        _gcp.PutObject(Bkt, WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/seq.bedgraph", worker);
        var request = TestInputs.Request(jobId, Spec(jobId));
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Downloading, DateTimeOffset.UtcNow, OutputDir: elsewhere), CancellationToken.None);

        await TransferFor().DownloadAsync(request, Bkt, Doc(worker, "output/seq/seq.bedgraph"), CancellationToken.None);

        Directory.GetFiles(elsewhere, "*", SearchOption.AllDirectories).ShouldBeEmpty("a folder outside the chosen output folder is never written to");
        var recorded = _runs.AllRecordedInOrder.Last(r => r.JobId == jobId).OutputDir!;
        RunOutputRoot.IsStrictlyInside(recorded, request.OutputFolder).ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(recorded, "seq", "seq.bedgraph")).ShouldBe(worker);
    }

    [Fact]
    public async Task A_recorded_output_folder_that_is_a_junction_is_not_followed()
    {
        const string jobId = "job-r2m1b";
        var request = TestInputs.Request(jobId, Spec(jobId));
        var target = Folder("m1-target");
        var link = Path.Combine(request.OutputFolder, "linked");
        if (!TryMakeJunction(link, target))
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        var worker = "worker bytes"u8.ToArray();
        _gcp.PutObject(Bkt, WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/seq.bedgraph", worker);
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Downloading, DateTimeOffset.UtcNow, OutputDir: link), CancellationToken.None);

        await TransferFor().DownloadAsync(request, Bkt, Doc(worker, "output/seq/seq.bedgraph"), CancellationToken.None);

        Directory.GetFiles(target, "*", SearchOption.AllDirectories).ShouldBeEmpty("a junction in the output folder is not followed");
    }

    // ---- M2: a probe error is not a stall ----

    [Fact]
    public async Task A_file_that_vanishes_or_is_locked_between_the_exists_check_and_the_length_read_is_not_a_stall()
    {
        var path = Path.Combine(_base, "m2.part");
        var calls = 0;
        long Probe(string p)
        {
            calls++;
            if (calls <= 2)
            {
                throw new IOException("the file vanished between the check and the read");
            }

            return calls;
        }

        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadToFileAsync("b", "k", path, Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            await Task.Delay(1500, ci.ArgAt<CancellationToken>(3));
            return true;
        });

        var found = await StreamedDownload.RunAsync(storage, TimeProvider.System, TestScopedGateways.Default, "b", "k", path, null, TimeSpan.FromMilliseconds(1000), CancellationToken.None, Probe).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        found.ShouldBeTrue("an IOException while reading the length is 'no progress observed yet', not a timeout");
        calls.ShouldBeGreaterThan(2, "vacuity: the probe really ran and threw");
    }

    [Fact]
    public async Task A_probe_that_keeps_failing_still_ends_in_the_stall_deadline_not_a_hang()
    {
        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadToFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(3));
            return true;
        });

        var ex = await Should.ThrowAsync<CloudOperationException>(() => StreamedDownload.RunAsync(storage, TimeProvider.System, TestScopedGateways.Default, "b", "k", Path.Combine(_base, "m2b.part"), null, TimeSpan.FromMilliseconds(200), CancellationToken.None, _ => throw new IOException("locked")).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
    }

    // ---- M3: the aside rename never blocks for tens of seconds ----

    private sealed class ScriptedMover(Func<string, string, int, Exception?> script) : IRunFileMover
    {
        public int Calls { get; private set; }

        public void Move(string source, string destination)
        {
            Calls++;
            if (script(source, destination, Calls) is { } error)
            {
                throw error;
            }

            File.Move(source, destination, overwrite: false);
        }
    }

    private sealed class SteppingClock(TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks += step.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    [Fact]
    public void A_move_that_fails_on_a_free_name_is_not_retried_under_fifty_other_names()
    {
        var file = Path.Combine(Folder("m3a"), "report.txt");
        File.WriteAllBytes(file, [1]);
        var mover = new ScriptedMover((_, _, _) => new IOException("sharing violation"));

        var clock = Stopwatch.StartNew();
        Should.Throw<IOException>(() => ChangedFileAside.Move(file, TimeProvider.System, mover));

        mover.Calls.ShouldBe(1, "a failure on a name nobody holds is not an 'exists' conflict; trying 49 more names just waits again");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        File.Exists(file).ShouldBeTrue();
    }

    [Fact]
    public void A_taken_name_moves_on_to_the_next_name_at_once()
    {
        var file = Path.Combine(Folder("m3b"), "report.txt");
        File.WriteAllBytes(file, [1]);
        var mover = new ScriptedMover((_, destination, n) =>
        {
            if (n < 3)
            {
                File.WriteAllBytes(destination, [9]);
                return new IOException("exists");
            }

            return null;
        });

        var moved = ChangedFileAside.Move(file, TimeProvider.System, mover);

        mover.Calls.ShouldBe(3);
        File.Exists(moved).ShouldBeTrue();
        File.Exists(file).ShouldBeFalse();
        moved.ShouldContain("(changed");
    }

    [Fact]
    public void The_total_time_spent_looking_for_a_free_name_is_bounded()
    {
        var file = Path.Combine(Folder("m3c"), "report.txt");
        File.WriteAllBytes(file, [1]);
        var mover = new ScriptedMover((_, destination, _) =>
        {
            File.WriteAllBytes(destination, [9]);
            return new IOException("exists");
        });

        // Each clock read advances 10 s: a bound of a few seconds is crossed after a handful of names, not fifty.
        Should.Throw<IOException>(() => ChangedFileAside.Move(file, new SteppingClock(TimeSpan.FromSeconds(10)), mover));

        mover.Calls.ShouldBeLessThan(5);
    }

    // ---- M4: the run's own download puts the user's file back, and says it renamed one ----

    private sealed class FailingFinalMove : IRunFileMover
    {
        private readonly DiskRunFileMover _disk = new();

        public void Move(string source, string destination)
        {
            if (source.EndsWith(".part", StringComparison.Ordinal))
            {
                throw new IOException("the final move failed");
            }

            _disk.Move(source, destination);
        }
    }

    [Fact]
    public async Task When_the_final_move_fails_the_users_edited_file_is_put_back_under_its_own_name()
    {
        const string jobId = "job-r2m4";
        var worker = "worker bytes"u8.ToArray();
        var edited = "the user edited this"u8.ToArray();
        _gcp.PutObject(Bkt, WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/seq.bedgraph", worker);
        var request = TestInputs.Request(jobId, Spec(jobId));
        var folder = Path.Combine(request.OutputFolder, "resumed");
        var destination = Path.Combine(folder, "seq", "seq.bedgraph");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, edited);
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Downloading, DateTimeOffset.UtcNow, OutputDir: folder), CancellationToken.None);

        var ex = await Should.ThrowAsync<RunFailureException>(() => TransferFor(new FailingFinalMove()).DownloadAsync(request, Bkt, Doc(worker, "output/seq/seq.bedgraph"), CancellationToken.None));

        ex.Code.ShouldBe(RunErrorCodes.DownloadFailed);
        File.ReadAllBytes(destination).ShouldBe(edited, "the user's file is never left renamed with nothing in its place");
        Directory.GetFiles(Path.GetDirectoryName(destination)!, "*(changed*").ShouldBeEmpty("the aside copy was moved back");
    }

    [Fact]
    public async Task A_download_that_renamed_a_changed_file_aside_is_recorded_with_the_code_that_says_so()
    {
        const string jobId = "job-r2m4b";
        var worker = "worker bytes"u8.ToArray();
        var edited = "the user edited this"u8.ToArray();
        _gcp.PutObject(Bkt, WorkerManifestBuilder.JobPrefix(jobId) + "output/seq/seq.bedgraph", worker);
        var request = TestInputs.Request(jobId, Spec(jobId), afterTask: AfterTaskAction.KeepAlive);
        var folder = Path.Combine(request.OutputFolder, "resumed");
        var destination = Path.Combine(folder, "seq", "seq.bedgraph");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, edited);
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Downloading, DateTimeOffset.UtcNow, OutputDir: folder), CancellationToken.None);
        var settings = new CloudRunSettings();
        var calls = new GatewayCalls(settings, _gcp);
        var rows = new RunRowStore(_runs, null);
        var recorder = new RunOutcomeRecorder(rows, new RunTransfer(_gcp, calls, settings, rows), new VmTerminator(_gcp, calls, settings));
        var json = $$"""{"schema":1,"status":"done","inputs":[{"id":"in1","status":"done","files":[{"path":"output/seq/seq.bedgraph","sha256":"{{Sha(worker)}}","bytes":{{worker.Length}}}]}]}""";

        var result = await recorder.FinishAsync(request, Bkt, null, json, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        var row = _runs.AllRecordedInOrder.Last(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Completed);
        row.ErrorCode.ShouldBe(RunErrorCodes.ChangedFilesKeptAside);
        File.ReadAllBytes(destination).ShouldBe(worker);
        File.ReadAllBytes(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*(changed*").Single()).ShouldBe(edited);
    }
}
