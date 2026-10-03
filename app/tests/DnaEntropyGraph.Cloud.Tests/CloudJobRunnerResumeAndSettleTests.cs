using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Round 3 of the cold review: a resumed run ends a VM that may already exist whatever fails, a create the runner gave up on
/// never lands as an orphan on a finished run (or a cancelled one), an unconfirmed VM end is told to the user through the
/// recorded code, every call has a deadline, and a VM that stopped between looks is judged like any lost VM.
/// </summary>
public class CloudJobRunnerResumeAndSettleTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, Action<string, JobPhase>? onPhase = null)
        => new(gcp, gcp, gcp, gcp, repo, onPhase)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            CallTimeout = TimeSpan.FromMilliseconds(50),
            CreateTimeout = TimeSpan.FromMilliseconds(50),
            CreateSettleTimeout = TimeSpan.FromSeconds(2),
            UploadTimeout = TimeSpan.FromMilliseconds(50),
            BootTimeout = TimeSpan.FromMilliseconds(80),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId)
        => (await gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;

    private static async Task<T> WithinAsync<T>(Task<T> task, string because)
    {
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        winner.ShouldBeSameAs(task, because);
        return await task;
    }

    /// <summary>A run that was Running when the app died: a RUNNING VM exists and the row says Running.</summary>
    private static async Task<CloudJobRequest> SeedRunningAsync(FakeGcp gcp, InMemoryRunRepository repo, string jobId)
    {
        var request = TestInputs.Request(jobId, Spec(jobId));
        await gcp.CreateVmAsync(request.Spec, Zone, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord(jobId, JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        return request;
    }

    // ---- 1: a resume ends a VM that may already exist whatever fails ----

    [Fact]
    public async Task A_hard_failure_looking_for_result_json_on_a_resume_ends_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedRunningAsync(gcp, repo, "job-r1");
        gcp.WithTryDownloadFailure(new CloudError("IAM_PERMISSION_DENIED", 403, "Required 'storage.objects.get' permission."), 1);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        (await VmStatusAsync(gcp, "job-r1")).ShouldBe("STOPPED", "a terminal Failed record means nothing will ever reach this VM again");
    }

    [Fact]
    public async Task A_transient_failure_looking_for_result_json_on_a_resume_does_not_end_the_run()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedRunningAsync(gcp, repo, "job-r2");
        gcp.WithTryDownloadFailure(new CloudError(null, 503, "The service is currently unavailable."), 1);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.ResultTimeout, "it got into the wait, which is what a surviving run does");
    }

    [Fact]
    public async Task A_hard_failure_finding_the_VM_on_a_resume_ends_the_VM_by_label()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedRunningAsync(gcp, repo, "job-r3");
        gcp.WithFindByJobIdFailure(new CloudError("IAM_PERMISSION_DENIED", 403, "Required 'compute.instances.list' permission."), 1);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("permission");
        (await VmStatusAsync(gcp, "job-r3")).ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_failed_preflight_on_a_resume_still_ends_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedRunningAsync(gcp, repo, "job-r4");
        gcp.WithBillingOff(Project);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FailureCode.ShouldBe("billing");
        (await VmStatusAsync(gcp, "job-r4")).ShouldBe("STOPPED");
    }

    // ---- 2: a create the runner gave up on never lands as an orphan ----

    [Fact]
    public async Task A_create_that_lands_after_its_deadline_is_deleted_before_the_run_is_recorded_Failed()
    {
        var gcp = new FakeGcp().WithCreateDelay(TimeSpan.FromMilliseconds(150));
        var repo = new InMemoryRunRepository();

        var result = await WithinAsync(Runner(gcp, repo).RunAsync(TestInputs.Request("job-s1", Spec("job-s1")), CancellationToken.None), "hung");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        (await gcp.FindByJobIdAsync("job-s1", CancellationToken.None)).ShouldBeEmpty("the late insert landed after the run ended: an orphan billing on a Failed run");
    }

    [Fact]
    public async Task A_cancel_waits_for_an_insert_still_in_flight_and_then_deletes_what_landed()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, repo);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(TestInputs.Request("job-s2", Spec("job-s2")), cts.Token);
        await gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        var cancel = runner.CancelAsync("job-s2", CancellationToken.None);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        cancel.IsCompleted.ShouldBeFalse("the cancel recorded Cancelled while its own insert could still land");
        gcp.ReleaseCreate();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        (await runner.GetPhaseAsync("job-s2", CancellationToken.None)).ShouldBe(JobPhase.Cancelled);
        (await gcp.FindByJobIdAsync("job-s2", CancellationToken.None)).ShouldBeEmpty();
    }

    // ---- 3: stop is not the end for a VM that never booted (covered in CloudJobRunnerHardeningTests) ----

    // ---- 4 and 5: an unconfirmed VM end leads the user to the Cloud page ----

    [Fact]
    public async Task A_failed_job_whose_VM_end_cannot_be_confirmed_is_recorded_under_vm_end_unconfirmed_with_the_cause_kept()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed).WithStopBehaviour(stoppingPolls: 1_000_000);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-u1", Spec("job-u1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed);
        var row = LastRow(repo, "job-u1");
        row.ErrorCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed);
        row.ErrorDetail.ShouldNotBeNull().ShouldContain("MODEL_OOM", Case.Sensitive);
    }

    [Fact]
    public async Task A_download_failure_whose_VM_end_cannot_be_confirmed_is_recorded_under_vm_end_unconfirmed()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.ChecksumMismatch).WithStopBehaviour(stoppingPolls: 1_000_000);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-u2", Spec("job-u2")), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed);
        LastRow(repo, "job-u2").ErrorDetail.ShouldNotBeNull().ShouldContain("download_corrupt");
    }

    [Fact]
    public async Task A_confirmed_VM_end_keeps_the_original_code()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-u3", Spec("job-u3")), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.ModelOom);
    }

    // ---- 6: every call has a deadline ----

    [Fact]
    public async Task A_hung_look_for_the_VMs_at_provisioning_fails_the_run_instead_of_hanging_it()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Provisioning)
            {
                gcp.WithHungCalls(1, ignoreCancellation: true);
            }
        });

        var result = await WithinAsync(runner.RunAsync(TestInputs.Request("job-t1", Spec("job-t1")), CancellationToken.None), "FindByJobId has no deadline");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("network");
        gcp.CreateAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_hung_upload_fails_the_run_instead_of_hanging_it()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Uploading)
            {
                gcp.WithHungCalls(1, ignoreCancellation: true);
            }
        });

        var result = await WithinAsync(runner.RunAsync(TestInputs.Request("job-t2", Spec("job-t2")), CancellationToken.None), "uploads have no deadline");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("network");
        gcp.CreateAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_hung_look_for_the_bucket_fails_the_run_instead_of_hanging_it()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Validating)
            {
                gcp.WithHungCalls(1, ignoreCancellation: true);
            }
        });

        var result = await WithinAsync(runner.RunAsync(TestInputs.Request("job-t3", Spec("job-t3")), CancellationToken.None), "EnsureBucket has no deadline");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task A_cancel_whose_VM_lookup_hangs_is_recorded_Failed_with_the_delete_it_yourself_code()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedRunningAsync(gcp, repo, "job-t4");
        gcp.WithHungCalls(1, ignoreCancellation: true);

        await WithinAsync(Runner(gcp, repo).CancelAsync("job-t4", CancellationToken.None).ContinueWith(_ => true, TestContext.Current.CancellationToken), "cancel hung");

        LastRow(repo, "job-t4").Phase.ShouldBe(JobPhase.Failed);
        LastRow(repo, "job-t4").ErrorCode.ShouldBe(RunErrorCodes.CancelFailed);
    }

    // ---- 7: a VM that is already stopped at the first look is judged like any lost VM ----

    [Fact]
    public async Task A_worker_that_finished_and_stopped_its_VM_before_the_first_look_is_downloaded()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        var spec = Spec("job-v1");
        var gcp = new FakeGcp(time).WithPreemption(spec.VmName, Zone, TimeSpan.Zero);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-v1", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        File.Exists(Path.Combine(LastRow(repo, "job-v1").OutputDir!, "seq", "seq.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_boot_failure_that_ended_the_VM_before_the_first_look_gets_its_mapped_code()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        var spec = Spec("job-v2");
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.GpuNotVisible).WithPreemption(spec.VmName, Zone, TimeSpan.Zero);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-v2", spec), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.GpuNotVisible);
    }

    // ---- 8: a done input must have a track ----

    [Fact]
    public async Task A_finished_input_that_lists_no_files_is_not_a_completed_run()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.DoneWithNoFiles);
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-w", Spec("job-w"));

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerFailed);
        LastRow(repo, "job-w").ErrorDetail.ShouldNotBeNull().ToLowerInvariant().ShouldContain("no result files");
        Directory.GetFileSystemEntries(request.OutputFolder).ShouldBeEmpty();
    }
}
