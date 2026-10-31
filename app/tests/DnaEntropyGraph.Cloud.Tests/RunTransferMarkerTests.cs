using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #934 cold review: a cloud run's fresh output folder carries the run's marker (so History can delete it), and a failed run still leaves no marker-only folder behind.</summary>
public sealed class RunTransferMarkerTests : IDisposable
{
    // Each test makes its run folders under its own temp root, which Dispose deletes.
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-transfer-marker-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();

    public RunTransferMarkerTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private (RunTransfer Transfer, CloudJobRequest Request) Setup(string jobId)
    {
        var request = TestInputs.Request(jobId, new VmSpec("my-project", "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE")) with
        {
            OutputFolder = Path.Combine(_base, "out"),
        };
        var settings = new CloudRunSettings();
        var transfer = new RunTransfer(_gcp, new GatewayCalls(settings, _gcp), settings, new RunRowStore(_runs, null));
        return (transfer, request);
    }

    private async Task<string> PreparedFolder(RunTransfer transfer, CloudJobRequest request)
    {
        await _runs.UpsertAsync(new RunRecord(request.JobId, JobPhase.Provisioning, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await transfer.PrepareRunFolderAsync(request, TestContext.Current.CancellationToken);
        return (await new RunRowStore(_runs, null).LatestRecordAsync(request.JobId, TestContext.Current.CancellationToken))!.OutputDir!;
    }

    [Fact]
    public async Task A_fresh_cloud_run_folder_carries_the_marker_of_its_job_and_History_may_delete_it()
    {
        const string jobId = "job-marker-1";
        var (transfer, request) = Setup(jobId);

        var folder = await PreparedFolder(transfer, request);

        RunFolderMarker.Check(folder, jobId).ShouldBe(RunFolderMarkerState.Matches);
        var row = (await new RunRowStore(_runs, null).LatestRecordAsync(jobId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        var files = new LocalRunFiles(NSubstitute.Substitute.For<IRunInputStore>(), () => request.OutputFolder, []);
        files.PlanDelete(row).ShouldBe(LocalDeletePlan.Proceed);
    }

    [Fact]
    public async Task A_failed_run_removes_its_marker_only_folder_and_forgets_it()
    {
        const string jobId = "job-marker-2";
        var (transfer, request) = Setup(jobId);
        var folder = await PreparedFolder(transfer, request);
        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue("precondition: the marker is there");

        await transfer.RemoveEmptyRunFolderAsync(jobId);

        Directory.Exists(folder).ShouldBeFalse("a folder holding only the marker is a leak in the user's output folder");
        (await new RunRowStore(_runs, null).LatestRecordAsync(jobId, TestContext.Current.CancellationToken))!.OutputDir.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_run_keeps_a_folder_that_holds_a_real_file()
    {
        const string jobId = "job-marker-3";
        var (transfer, request) = Setup(jobId);
        var folder = await PreparedFolder(transfer, request);
        await File.WriteAllTextAsync(Path.Combine(folder, "keep.txt"), "x", TestContext.Current.CancellationToken);

        await transfer.RemoveEmptyRunFolderAsync(jobId);

        File.Exists(Path.Combine(folder, "keep.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_run_never_removes_a_folder_whose_marker_names_another_run()
    {
        const string jobId = "job-marker-4";
        var (transfer, _) = Setup(jobId);
        var foreign = Path.Combine(_base, "someone-elses");
        Directory.CreateDirectory(foreign);
        RunFolderMarker.Write(foreign, "another-job");
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Failed, DateTimeOffset.UtcNow, OutputDir: foreign), TestContext.Current.CancellationToken);

        await transfer.RemoveEmptyRunFolderAsync(jobId);

        Directory.Exists(foreign).ShouldBeTrue("a damaged row naming another run's empty folder must not delete it");
        RunFolderMarker.Check(foreign, "another-job").ShouldBe(RunFolderMarkerState.Matches);
    }
}
