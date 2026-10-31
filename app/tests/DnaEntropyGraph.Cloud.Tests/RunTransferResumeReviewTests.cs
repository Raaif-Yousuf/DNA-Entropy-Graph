using System.Diagnostics;
using System.Security.Cryptography;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #588 merge review: resuming a run into its recorded folder works for a whole-drive output folder, a path check that touches the disk
/// cannot escape resume as an exception, and a junction swapped in after the folder was resolved is noticed before the result lands.
/// </summary>
public sealed class RunTransferResumeReviewTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-588resume-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();
    private static readonly string Bkt = FakeGcp.BucketName("my-project");

    public RunTransferResumeReviewTests() => Directory.CreateDirectory(_base);

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

    [Fact]
    public void A_recorded_folder_under_a_whole_drive_output_folder_is_reused()
    {
        RunTransfer.IsInsideOutputFolder(@"Q:\stem", @"Q:\").ShouldBeTrue("a user who picked a whole drive keeps resume");
        RunTransfer.IsInsideOutputFolder(@"R:\stem", @"Q:\").ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(DiskErrors))]
    public void A_containment_check_that_fails_on_the_disk_means_not_reusable_instead_of_an_escaping_exception(Exception failure)
    {
        RunTransfer.IsInsideOutputFolder(@"C:\x\y", @"C:\x", (_, _) => throw failure).ShouldBeFalse();
    }

    public static TheoryData<Exception> DiskErrors() => new()
    {
        new IOException("the share went away"),
        new UnauthorizedAccessException("no access"),
        new PathTooLongException(),
        new ArgumentException("bad path"),
        new NotSupportedException(),
    };

    [Fact]
    public async Task A_junction_swapped_in_after_the_folder_was_resolved_is_noticed_and_nothing_lands_in_its_target()
    {
        const string jobId = "job-resume-swap";
        var request = TestInputs.Request(jobId, new VmSpec("my-project", "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE"));
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        var worker = "worker bytes"u8.ToArray();
        var doc = new WorkerResultDocument("done", [new WorkerInputResult("in1", "done", [new WorkerResultFile("output/seq/seq.bedgraph", Convert.ToHexString(SHA256.HashData(worker)), worker.Length)], null, null)], null, null);

        var swapped = false;
        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadToFileAsync(Arg.Any<CloudScope>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var partial = ci.ArgAt<string>(3);
            File.WriteAllBytes(partial, worker);
            var seq = Path.GetDirectoryName(partial)!;

            // After the folder was resolved and the first check passed: the result folder is replaced by a junction to somewhere else.
            Directory.Move(seq, seq + "-moved");
            swapped = TryMakeJunction(seq, outside);
            return Task.FromResult(true);
        });

        var settings = new CloudRunSettings();
        var transfer = new RunTransfer(storage, new GatewayCalls(settings, _gcp), settings, new RunRowStore(_runs, null), null);

        var failure = await Should.ThrowAsync<RunFailureException>(() => transfer.DownloadAsync(request, Bkt, doc, CancellationToken.None));

        if (!swapped)
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        failure.Message.ShouldContain("linked folder");
        Directory.GetFiles(outside, "*", SearchOption.AllDirectories).ShouldBeEmpty("nothing lands in the junction's target");
    }

    [Theory]
    [InlineData(@"Q:\a", @"Q:\", true)]
    [InlineData(@"Q:\a\b", @"Q:\", false)]
    [InlineData(@"C:\out\stem", @"C:\out", true)]
    [InlineData(@"C:\out\stem\deeper", @"C:\out", false)]
    [InlineData(@"C:\out", @"C:\out", false)]
    [InlineData(@"C:\other\stem", @"C:\out", false)]
    public void Resume_reuses_only_a_direct_child_of_the_output_folder(string folder, string outputFolder, bool expected)
    {
        // The run folder is always created directly under the output folder (CreateUnique), so a deeper recorded path is not one of ours.
        RunTransfer.IsInsideOutputFolder(folder, outputFolder).ShouldBe(expected);
    }

    [Fact]
    public void A_link_check_that_fails_on_the_disk_is_the_runs_download_failure_not_a_raw_exception()
    {
        var ex = Should.Throw<RunFailureException>(() => RunTransfer.EnsureNoLinkedFolder(@"C:\run", @"C:\run\seq\x", "Result file 1 of input in1", (_, _) => throw new UnauthorizedAccessException("no access")));

        ex.Code.ShouldBe(RunErrorCodes.DownloadFailed);
    }

    [Fact]
    public async Task The_run_folder_itself_swapped_for_a_junction_is_noticed_and_nothing_lands_in_its_target()
    {
        const string jobId = "job-resume-swap-root";
        var request = TestInputs.Request(jobId, new VmSpec("my-project", "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE"));
        var outside = Path.Combine(_base, "outside-root");
        Directory.CreateDirectory(Path.Combine(outside, "seq"));
        var worker = "worker bytes"u8.ToArray();
        var doc = new WorkerResultDocument("done", [new WorkerInputResult("in1", "done", [new WorkerResultFile("output/seq/seq.bedgraph", Convert.ToHexString(SHA256.HashData(worker)), worker.Length)], null, null)], null, null);

        var swapped = false;
        var storage = Substitute.For<IStorageGateway>();
        storage.TryDownloadToFileAsync(Arg.Any<CloudScope>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var partial = ci.ArgAt<string>(3);
            File.WriteAllBytes(partial, worker);
            var runFolder = Path.GetDirectoryName(Path.GetDirectoryName(partial))!;

            // The run folder itself (not a folder below it) becomes a junction, so no component below it is a link.
            Directory.Move(runFolder, runFolder + "-moved");
            swapped = TryMakeJunction(runFolder, outside);
            return Task.FromResult(true);
        });

        var settings = new CloudRunSettings();
        var transfer = new RunTransfer(storage, new GatewayCalls(settings, _gcp), settings, new RunRowStore(_runs, null), null);

        var failure = await Should.ThrowAsync<RunFailureException>(() => transfer.DownloadAsync(request, Bkt, doc, CancellationToken.None));

        if (!swapped)
        {
            Assert.Skip("A junction could not be created on this machine.");
        }

        failure.Message.ShouldContain("linked folder");
        Directory.GetFiles(outside, "*", SearchOption.AllDirectories).ShouldBeEmpty("nothing lands in the junction's target");
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
}
