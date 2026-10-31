using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #934 cold review: a cloud run's fresh output folder carries the run's marker (so History can delete it), and a failed run still leaves no marker-only folder behind.</summary>
public sealed class RunTransferMarkerTests
{
    private readonly FakeGcp _gcp = new();
    private readonly InMemoryRunRepository _runs = new();

    private (RunTransfer Transfer, CloudJobRequest Request) Setup(string jobId)
    {
        var request = TestInputs.Request(jobId, new VmSpec("my-project", "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE"));
        var settings = new CloudRunSettings();
        var transfer = new RunTransfer(_gcp, new GatewayCalls(settings, _gcp), settings, new RunRowStore(_runs, null));
        return (transfer, request);
    }

    [Fact]
    public async Task A_fresh_cloud_run_folder_carries_the_marker_of_its_job_and_History_may_delete_it()
    {
        const string jobId = "job-marker-1";
        var (transfer, request) = Setup(jobId);
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);

        await transfer.PrepareRunFolderAsync(request, CancellationToken.None);

        var row = (await new RunRowStore(_runs, null).LatestRecordAsync(jobId, CancellationToken.None)).ShouldNotBeNull();
        var folder = row.OutputDir.ShouldNotBeNull();
        RunFolderMarker.Check(folder, jobId).ShouldBe(RunFolderMarkerState.Matches);

        var files = new LocalRunFiles(NSubstitute.Substitute.For<DnaEntropyGraph.Core.Abstractions.IRunInputStore>(), () => request.OutputFolder, []);
        files.PlanDelete(row).ShouldBe(LocalDeletePlan.Proceed);
    }

    [Fact]
    public async Task A_failed_run_removes_its_marker_only_folder_and_forgets_it()
    {
        const string jobId = "job-marker-2";
        var (transfer, request) = Setup(jobId);
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);
        await transfer.PrepareRunFolderAsync(request, CancellationToken.None);
        var folder = (await new RunRowStore(_runs, null).LatestRecordAsync(jobId, CancellationToken.None))!.OutputDir!;
        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue("precondition: the marker is there");

        await transfer.RemoveEmptyRunFolderAsync(jobId);

        Directory.Exists(folder).ShouldBeFalse("a folder holding only the marker is a leak in the user's output folder");
        (await new RunRowStore(_runs, null).LatestRecordAsync(jobId, CancellationToken.None))!.OutputDir.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_run_keeps_a_folder_that_holds_a_real_file()
    {
        const string jobId = "job-marker-3";
        var (transfer, request) = Setup(jobId);
        await _runs.UpsertAsync(new RunRecord(jobId, JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);
        await transfer.PrepareRunFolderAsync(request, CancellationToken.None);
        var folder = (await new RunRowStore(_runs, null).LatestRecordAsync(jobId, CancellationToken.None))!.OutputDir!;
        await File.WriteAllTextAsync(Path.Combine(folder, "keep.txt"), "x", TestContext.Current.CancellationToken);

        await transfer.RemoveEmptyRunFolderAsync(jobId);

        File.Exists(Path.Combine(folder, "keep.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue();
    }
}
