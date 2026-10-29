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

    private static readonly TimeSpan CallLimit = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A runner on test time (#525, #567): every deadline in these tests is on <see cref="RunnerClocks.Run"/>, which moves when the
    /// runner sleeps between polls or when a call the test scripted to park is waiting, so a loaded machine cannot cut a healthy call.
    /// A test whose fake must share the runner clock (a slow create) passes its own <paramref name="clocks"/>.
    /// </summary>
    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, Action<string, JobPhase>? onPhase = null, RunnerClocks? clocks = null)
    {
        clocks ??= new RunnerClocks();
        clocks.ParkPump(gcp, CallLimit, SettleLimit);
        return new(gcp, gcp, gcp, gcp, gcp, repo, onPhase)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            CallTimeout = CallLimit,
            EnsureBucketTimeout = TimeSpan.FromMilliseconds(50),
            CreateTimeout = TimeSpan.FromMilliseconds(50),
            CreateSettleTimeout = SettleLimit,
            UploadTimeout = TimeSpan.FromMilliseconds(50),
            BootTimeout = TimeSpan.FromMilliseconds(80),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };
    }

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
        // The fake shares the runner clock: the pump moves it past the create deadline (50 ms) once the runner is waiting on it. The
        // insert is then held on a gate, released only when the runner has armed its settle window on the abandoned create (or the
        // run is over), so "lands after the deadline and after the runner gave up" is a fact of ordering, never of a few virtual ms.
        var clocks = new RunnerClocks();
        var landing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gcp = new FakeGcp(clocks.Run).WithCreateDelay(TimeSpan.FromMilliseconds(55));
        gcp.CreateLandingGate = landing.Task;
        var repo = new InMemoryRunRepository();

        var run = Runner(gcp, repo, clocks: clocks).RunAsync(TestInputs.Request("job-s1", Spec("job-s1")), CancellationToken.None);
        _ = Task.WhenAny(run, clocks.Run.WhenTimerCreatedAsync(0, SettleLimit)).ContinueWith(_ => landing.TrySetResult(), TaskScheduler.Default);
        var result = await WithinAsync(run, "hung");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        landing.TrySetResult();
        await gcp.WhenDelayedCreateLandedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        (await gcp.FindByJobIdAsync("job-s1", CancellationToken.None)).ShouldBeEmpty("the late insert landed after the run ended: an orphan billing on a Failed run");
    }

    [Fact]
    public async Task A_VM_that_was_created_and_then_lost_inside_provisioning_still_has_its_start_recorded_for_the_spend_ledger()
    {
        var gcp = new FakeGcp().WithCreateDelay(TimeSpan.FromMilliseconds(150));
        var repo = new InMemoryRunRepository();

        var result = await WithinAsync(Runner(gcp, repo).RunAsync(TestInputs.Request("job-s3", Spec("job-s3")), CancellationToken.None), "hung");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        var row = LastRow(repo, "job-s3");
        row.VmReadyAt.ShouldNotBeNull("the late create landed, billed until it was deleted, and the failed row must say when it started");
        row.FinishedAt.ShouldNotBeNull();
        row.VmReadyAt!.Value.ShouldBeLessThanOrEqualTo(row.FinishedAt!.Value);
    }

    [Fact]
    public async Task A_run_that_never_got_a_VM_has_no_start_recorded()
    {
        var gcp = new FakeGcp().WithZoneStockout(Zone);
        var repo = new InMemoryRunRepository();

        var result = await WithinAsync(Runner(gcp, repo).RunAsync(TestInputs.Request("job-s4", Spec("job-s4")), CancellationToken.None), "hung");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        LastRow(repo, "job-s4").VmReadyAt.ShouldBeNull("a stockout creates nothing, so nothing is booked");
    }

    [Fact]
    public async Task A_cancel_waits_for_an_insert_still_in_flight_and_then_deletes_what_landed()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(TestInputs.Request("job-s2", Spec("job-s2")), cts.Token);
        await gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        var cancel = runner.CancelAsync("job-s2", CancellationToken.None);
        // Real time is kept on purpose: a negative check that can only pass too early under load (a cancel that wrongly finished
        // would still be seen), never fail a correct run. The cancel itself has no clock-bound step here.
        await Task.Delay(150, TestContext.Current.CancellationToken);
        cancel.IsCompleted.ShouldBeFalse("the cancel recorded Cancelled while its own insert could still land");
        gcp.ReleaseCreate();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        (await runner.GetPhaseAsync("job-s2", CancellationToken.None)).ShouldBe(JobPhase.Cancelled);
        (await gcp.FindByJobIdAsync("job-s2", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancel_while_the_create_is_in_flight_records_when_the_VM_that_landed_started_so_the_ledger_books_it()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(TestInputs.Request("job-s5", Spec("job-s5")), cts.Token);
        await gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        var cancel = runner.CancelAsync("job-s5", CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        gcp.ReleaseCreate();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var row = LastRow(repo, "job-s5");
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.VmReadyAt.ShouldNotBeNull("the create landed and billed until the cancel deleted it; without a start the ledger books nothing");
        row.VmReadyAt!.Value.ShouldBeLessThanOrEqualTo(row.FinishedAt!.Value);
    }

    [Fact]
    public async Task A_cancel_of_a_run_that_never_created_a_VM_records_no_start()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo);
        await repo.UpsertAsync(new RunRecord("job-s6", JobPhase.Uploading, DateTimeOffset.UtcNow, Target: "cloud"), CancellationToken.None);

        await runner.CancelAsync("job-s6", CancellationToken.None);

        var row = LastRow(repo, "job-s6");
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.VmReadyAt.ShouldBeNull();
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

    // ---- #109 round 3: the span booked is what the VM ran, not how long the app was closed ----

    private static async Task SeedFinishedAndStoppedAsync(FakeGcp gcp, InMemoryRunRepository repo, ManualTimeProvider time, string jobId, TimeSpan ranFor, bool stop = true)
    {
        var request = TestInputs.Request(jobId, Spec(jobId));
        await gcp.CreateVmAsync(request.Spec, Zone, CancellationToken.None);
        time.Advance(ranFor);
        if (stop)
        {
            await gcp.StopVmAsync($"deg-{jobId}", Zone, CancellationToken.None);
        }

        var bucket = FakeGcp.BucketName(Project);
        var track = FakeGcp.TrackBytes("seq");
        gcp.PutObject(bucket, $"jobs/{jobId}/output/seq/seq.bedgraph", track);
        gcp.PutObject(bucket, $"jobs/{jobId}/result.json", System.Text.Encoding.UTF8.GetBytes(
            $$"""{"schema":1,"jobId":"{{jobId}}","status":"done","inputs":[{"id":"in1","status":"done","outputs":["output/seq/seq.bedgraph"],"files":[{"path":"output/seq/seq.bedgraph","sha256":"{{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(track))}}","bytes":{{track.Length}}}],"notices":[],"stats":null,"error":null}],"error":null}"""));
        await repo.UpsertAsync(new RunRecord(jobId, JobPhase.Running, DateTimeOffset.UtcNow.AddHours(-10)), CancellationToken.None);
    }

    [Fact]
    public async Task A_run_resumed_hours_after_its_VM_stopped_itself_books_the_span_the_VM_ran_not_the_time_the_app_was_closed()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);
        var time = new ManualTimeProvider(t0);
        var gcp = new FakeGcp(time);
        var repo = new InMemoryRunRepository();
        await SeedFinishedAndStoppedAsync(gcp, repo, time, "job-s7", TimeSpan.FromMinutes(20));

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-s7", Spec("job-s7")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var row = LastRow(repo, "job-s7");
        row.VmReadyAt.ShouldBe(t0, "the VM's own platform start");
        (row.FinishedAt!.Value - row.VmReadyAt!.Value).TotalMinutes.ShouldBe(20, 0.01, "the VM stopped at +20 min; resuming 10 h later must not book 10 h");
    }

    [Fact]
    public async Task A_resumed_finished_run_whose_VM_is_still_running_ends_now()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await SeedFinishedAndStoppedAsync(gcp, repo, time, "job-s8", TimeSpan.Zero, stop: false);
        var request = TestInputs.Request("job-s8", Spec("job-s8"));
        (await gcp.FindByJobIdAsync("job-s8", CancellationToken.None)).ShouldNotBeEmpty();

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var row = LastRow(repo, "job-s8");
        (DateTimeOffset.UtcNow - row.FinishedAt!.Value).ShouldBeLessThan(TimeSpan.FromMinutes(1), "a running VM bills until the run ends: the end is the finish");
    }
}
