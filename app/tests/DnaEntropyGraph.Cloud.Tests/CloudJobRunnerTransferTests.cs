using System.Text.Json;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #460: a run uploads the real input and the manifest before any VM
/// exists, waits for the worker's result.json, and downloads the outputs to
/// the chosen folder. The fake bucket holds the bytes, so each test asserts
/// what a user would find (or a bucket would hold), not that a method ran.
/// Every failure ends the run in a recorded terminal state (Hard Rule 11).
/// </summary>
public class CloudJobRunnerTransferTests
{
    private const string Project = "my-project";

    private static VmSpec Spec(string jobId, TimeSpan? maxRun = null) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: maxRun ?? TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static string Bucket => FakeGcp.BucketName(Project);

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, Action<string, JobPhase>? onPhase = null)
        => new(gcp, gcp, gcp, gcp, repo, onPhase)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(60),
        };

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    [Fact]
    public async Task The_bucket_holds_the_manifest_and_the_exact_input_bytes_before_any_VM_exists()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-up", Spec("job-up"));
        IReadOnlyList<string>? keysAtProvisioning = null;
        var vmCountAtProvisioning = -1;
        var runner = Runner(gcp, repo, (id, phase) =>
        {
            if (phase == JobPhase.Provisioning)
            {
                keysAtProvisioning = gcp.ObjectKeys(Bucket);
                vmCountAtProvisioning = gcp.FindByJobIdAsync(id, CancellationToken.None).Result.Count;
            }
        });

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        vmCountAtProvisioning.ShouldBe(0);
        keysAtProvisioning.ShouldNotBeNull();
        keysAtProvisioning!.ShouldContain("jobs/job-up/manifest.json");
        keysAtProvisioning.ShouldContain("jobs/job-up/input/seq.gb");

        gcp.GetObjectBytes(Bucket, "jobs/job-up/input/seq.gb").ShouldBe(File.ReadAllBytes(request.Inputs[0].LocalPath));
        gcp.GetObjectBytes(Bucket, "jobs/job-up/input/seq.gb").Length.ShouldBeGreaterThan(10, "an empty upload is the bug this issue is about");

        using var manifest = JsonDocument.Parse(gcp.GetObjectBytes(Bucket, "jobs/job-up/manifest.json"));
        manifest.RootElement.GetProperty("jobId").GetString().ShouldBe("job-up");
        manifest.RootElement.GetProperty("inputs")[0].GetProperty("path").GetString().ShouldBe("input/seq.gb");
        manifest.RootElement.GetProperty("store").GetProperty("bucket").GetString().ShouldBe(Bucket);
        manifest.RootElement.GetProperty("createdBy").GetProperty("installationId").GetString().ShouldBe("install-1");
    }

    [Fact]
    public async Task The_run_row_records_the_bucket_prefix_and_manifest()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();

        await Runner(gcp, repo).RunAsync(TestInputs.Request("job-row", Spec("job-row")), CancellationToken.None);

        var row = LastRow(repo, "job-row");
        row.Bucket.ShouldBe(Bucket);
        row.JobPrefix.ShouldBe("jobs/job-row/");
        row.ManifestJson.ShouldNotBeNull();
        row.ManifestJson!.ShouldContain("\"jobId\":\"job-row\"");
    }

    [Fact]
    public async Task The_output_folder_holds_the_downloaded_track_and_the_run_row_names_it()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-down", Spec("job-down"));

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        var row = LastRow(repo, "job-down");
        row.OutputDir.ShouldNotBeNull();
        Path.GetDirectoryName(row.OutputDir!).ShouldBe(request.OutputFolder);
        var track = Path.Combine(row.OutputDir!, "seq", "seq.bedgraph");
        File.Exists(track).ShouldBeTrue("the track must be on disk where the user chose");
        File.ReadAllBytes(track).ShouldBe(FakeGcp.TrackBytes("seq"));
    }

    [Fact]
    public async Task A_second_run_with_the_same_input_name_gets_its_own_folder_and_leaves_the_first_alone()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var first = TestInputs.Request("job-d1", Spec("job-d1"));
        var second = TestInputs.Request("job-d2", Spec("job-d2")) with { OutputFolder = first.OutputFolder };

        await Runner(gcp, repo).RunAsync(first, CancellationToken.None);
        await Runner(gcp, repo).RunAsync(second, CancellationToken.None);

        var a = LastRow(repo, "job-d1").OutputDir;
        var b = LastRow(repo, "job-d2").OutputDir;
        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        b.ShouldNotBe(a);
        File.Exists(Path.Combine(a!, "seq", "seq.bedgraph")).ShouldBeTrue();
        File.Exists(Path.Combine(b!, "seq", "seq.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_the_run_and_leaves_no_file_behind()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.ChecksumMismatch);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-sum", Spec("job-sum")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("download_corrupt");
        LastRow(repo, "job-sum").ErrorCode.ShouldBe("download_corrupt");
        (await gcp.GetVmAsync("deg-job-sum", "us-central1-a", CancellationToken.None))!.Status.ShouldNotBe("RUNNING", "a failed download must still end the VM");
        var dir = LastRow(repo, "job-sum").OutputDir;
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ShouldBeEmpty("a corrupt download must not be left looking like a result");
        }
    }

    [Fact]
    public async Task A_result_path_that_climbs_out_of_the_output_folder_is_refused()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.UnsafePath);
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-esc", Spec("job-esc"));

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("worker_failed", "a path outside output/ is the worker breaking the contract, not a folder the user can free up");
        File.Exists(Path.Combine(request.OutputFolder, "evil.txt")).ShouldBeFalse();
        Directory.GetFiles(request.OutputFolder, "evil*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_worker_that_reports_failure_ends_the_run_Failed_and_the_VM_is_stopped()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-wf", Spec("job-wf")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("model_oom");
        LastRow(repo, "job-wf").ErrorDetail.ShouldNotBeNull().ShouldContain("MODEL_OOM", Case.Sensitive);
        (await gcp.GetVmAsync("deg-job-wf", "us-central1-a", CancellationToken.None))!.Status.ShouldBe("STOPPED", "a failed run must not leave a VM billing (Hard Rule 11)");
    }

    [Fact]
    public async Task A_result_that_cannot_be_read_ends_the_run_Failed_instead_of_completing()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.GarbageResult);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-gr", Spec("job-gr")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("worker_failed");
        (await gcp.GetVmAsync("deg-job-gr", "us-central1-a", CancellationToken.None))!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_worker_that_never_answers_ends_the_run_Failed_and_stops_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-nv", Spec("job-nv")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("result_timeout");
        LastRow(repo, "job-nv").Phase.ShouldBe(JobPhase.Failed);
        (await gcp.GetVmAsync("deg-job-nv", "us-central1-a", CancellationToken.None))!.Status.ShouldBe("STOPPED", "the timeout path must not leave the VM running");
    }

    [Fact]
    public async Task A_VM_lost_while_waiting_for_the_result_fails_the_run_as_unhealthy()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        var spec = Spec("job-lost");
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never).WithPreemption(spec.VmName, "us-central1-a", TimeSpan.FromHours(1));
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Running)
            {
                time.Advance(TimeSpan.FromHours(2));
            }
        });

        var result = await runner.RunAsync(TestInputs.Request("job-lost", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("vm_unhealthy");
    }

    [Fact]
    public async Task One_failed_input_in_a_batch_ends_PartiallyCompleted_and_only_the_good_input_is_downloaded()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.SecondInputFailed);
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-part", Spec("job-part"), null, AfterTaskAction.Stop, null, "a.gb", "b.gb");

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.PartiallyCompleted);
        var dir = LastRow(repo, "job-part").OutputDir!;
        File.Exists(Path.Combine(dir, "a", "a.bedgraph")).ShouldBeTrue();
        Directory.Exists(Path.Combine(dir, "b")).ShouldBeFalse();
    }

    [Fact]
    public async Task Every_input_failing_is_a_Failed_run_not_a_completed_one()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.AllInputsFailed);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-all", Spec("job-all")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("model_oom");
    }

    [Fact]
    public async Task An_input_that_vanished_before_upload_fails_the_run_before_any_VM_is_created()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-gone", Spec("job-gone"));
        File.Delete(request.Inputs[0].LocalPath);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("input_missing");
        LastRow(repo, "job-gone").ErrorCode.ShouldBe("input_missing");
        (await gcp.FindByJobIdAsync("job-gone", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_resumed_after_Uploading_never_rewrites_the_manifest()
    {
        // docs/job_contract.md section 3: the manifest is immutable once the worker may be reading it.
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var original = "ORIGINAL"u8.ToArray();
        gcp.PutObject(Bucket, "jobs/job-res/manifest.json", original);
        await repo.UpsertAsync(new RunRecord("job-res", JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);

        await Runner(gcp, repo).RunAsync(TestInputs.Request("job-res", Spec("job-res")), CancellationToken.None);

        gcp.GetObjectBytes(Bucket, "jobs/job-res/manifest.json").ShouldBe(original);
        gcp.ObjectKeys(Bucket).ShouldNotContain("jobs/job-res/input/seq.gb", "a resumed run past Uploading must not upload again");
    }

    [Fact]
    public async Task A_resumed_download_reuses_the_run_folder_instead_of_creating_a_second_one()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-rd", Spec("job-rd"));
        var firstRunner = Runner(gcp, repo);
        await firstRunner.RunAsync(request, CancellationToken.None);
        var dir = LastRow(repo, "job-rd").OutputDir!;

        // Simulate a crash after Downloading was recorded: the row is back in Downloading with its folder.
        await repo.UpsertAsync(LastRow(repo, "job-rd") with { Phase = JobPhase.Downloading, FinishedAt = null, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(1) }, CancellationToken.None);
        var resumed = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        resumed.FinalPhase.ShouldBe(JobPhase.Completed, "the resume must really run; without this the assertions below pass because nothing happened");
        LastRow(repo, "job-rd").OutputDir.ShouldBe(dir);
        Directory.GetDirectories(request.OutputFolder).Length.ShouldBe(1);
    }
}
