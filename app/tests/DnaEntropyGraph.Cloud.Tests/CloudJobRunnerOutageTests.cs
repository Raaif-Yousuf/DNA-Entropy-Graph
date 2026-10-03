using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// The cold review of the #460 transfer work (findings 1 to 4 and 12): what a run does when the
/// network flaps while it waits, when the worker has already stopped or deleted its own VM, when
/// one call hangs, when the VM boots but never works, and when no real cloud is connected. Each
/// test asserts what a user would find (the track on disk, the recorded code, the VM's state),
/// never that a method ran.
/// </summary>
public class CloudJobRunnerOutageTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

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

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    /// <summary>
    /// The clocks of one test run (#525). <see cref="Run"/> is the runner's: call deadlines, the result and lifecycle waits.
    /// It moves only when the runner sleeps between polls, never while a call is in flight, so a loaded machine cannot
    /// make a call "time out". <see cref="Retry"/> is the retry pipeline's: its backoff fires at once and the breaker's
    /// break period passes as the runner polls. Only the test whose subject is a call that really never
    /// answers runs the runner on real time.
    /// </summary>
    private sealed class Clocks
    {
        public VirtualTimeProvider Run { get; } = new();

        public VirtualTimeProvider Retry { get; } = new(autoAdvance: true);

        public Task SleepAsync(TimeSpan span, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Run.Advance(span);
            Retry.Advance(span);
            return Task.CompletedTask;
        }
    }

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, Action<string, JobPhase>? onPhase = null, TimeSpan? resultTimeout = null, bool realTime = false)
    {
        var clocks = new Clocks();
        return new(gcp, gcp, gcp, gcp, repo, onPhase)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = resultTimeout ?? TimeSpan.FromMilliseconds(80),
            CallTimeout = TimeSpan.FromMilliseconds(50),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            TimeProvider = realTime ? TimeProvider.System : clocks.Run,
            PollDelay = realTime ? null : clocks.SleepAsync,
        };
    }

    /// <summary>The production wiring: every gateway behind the one retry pipeline, with delays short enough for a test.</summary>
    private static CloudJobRunner RunnerThroughRetryPipeline(FakeGcp gcp, InMemoryRunRepository repo, Action<string, JobPhase>? onPhase, TimeSpan resultTimeout, bool breaker = false)
    {
        var clocks = new Clocks();
        var options = new CloudRetryOptions
        {
            TimeProvider = clocks.Retry,
            MaxRetryAttempts = 2,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(2),
            BreakDuration = TimeSpan.FromMilliseconds(500),
            BreakerMinimumThroughput = breaker ? 6 : 100_000,
        };
        var pipeline = new CloudCallPipeline(options, gcp, new CloudRetryLog());
        return new CloudJobRunner(
            new ResilientComputeGateway(gcp, pipeline),
            new ResilientStorageGateway(gcp, pipeline),
            new ResilientProjectSetupGateway(gcp, pipeline),
            new ResilientQuotaGateway(gcp, pipeline),
            repo,
            onPhase)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = resultTimeout,
            CallTimeout = TimeSpan.FromMilliseconds(50),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };
    }

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId)
        => (await gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;

    private static string Track(InMemoryRunRepository repo, string jobId, string input = "seq")
        => Path.Combine(LastRow(repo, jobId).OutputDir ?? "no-output-dir", input, input + ".bedgraph");

    // ---- Finding 1: a transient outage while waiting must not end the run ----

    [Fact]
    public async Task More_transient_503s_than_the_retry_pipeline_absorbs_do_not_end_a_run_whose_result_is_in_the_bucket()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = RunnerThroughRetryPipeline(
            gcp,
            repo,
            (_, phase) =>
            {
                if (phase == JobPhase.Running)
                {
                    gcp.WithTransientFailures(503, 14);
                }
            },
            TimeSpan.FromSeconds(30),
            breaker: true);

        var result = await runner.RunAsync(TestInputs.Request("job-flap", Spec("job-flap")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        File.ReadAllBytes(Track(repo, "job-flap")).ShouldBe(FakeGcp.TrackBytes("seq"));
    }

    [Fact]
    public async Task An_outage_that_outlasts_the_wait_ends_the_run_in_a_recorded_failure_and_says_the_VM_end_was_not_confirmed()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var runner = RunnerThroughRetryPipeline(
            gcp,
            repo,
            (_, phase) =>
            {
                if (phase == JobPhase.Running)
                {
                    gcp.WithTransientFailures(503, int.MaxValue);
                }
            },
            TimeSpan.FromMilliseconds(150));

        var result = await runner.RunAsync(TestInputs.Request("job-out", Spec("job-out")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed, "the VM could not be reached, so 'we stopped it' would be a claim nobody checked");
        LastRow(repo, "job-out").Phase.ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task A_permission_failure_while_waiting_still_aborts_at_once()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var runner = RunnerThroughRetryPipeline(
            gcp,
            repo,
            (_, phase) =>
            {
                if (phase == JobPhase.Running)
                {
                    gcp.WithUnauthorized(1000);
                }
            },
            TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(TestInputs.Request("job-perm", Spec("job-perm")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        // Every call is refused, so the VM end cannot be confirmed either: the user is sent to the Cloud page, the cause is kept.
        result.FailureCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed);
        LastRow(repo, "job-perm").ErrorDetail.ShouldNotBeNull().ShouldContain("Original code: permission");
    }

    // ---- Finding 2: download first, verify the lifecycle after, tolerate a worker that already acted ----

    [Fact]
    public async Task A_worker_that_deleted_its_own_VM_does_not_stop_the_download_when_Delete_was_chosen()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete).WithDeleteOfMissingVmNotFound();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-wd", Spec("job-wd"), null, AfterTaskAction.Delete);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        result.FailureCode.ShouldBeNull();
        File.Exists(Track(repo, "job-wd")).ShouldBeTrue();
        (await gcp.FindByJobIdAsync("job-wd", CancellationToken.None)).ShouldBeEmpty();
        LastRow(repo, "job-wd").ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task A_delete_that_loses_a_race_with_the_worker_deleting_itself_is_a_delete_done_not_a_failure()
    {
        var gcp = new FakeGcp().WithNextDeleteRacingAWorkerDelete();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-race", Spec("job-race"), null, AfterTaskAction.Delete);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        result.FailureCode.ShouldBeNull();
        (await gcp.FindByJobIdAsync("job-race", CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("STOPPED", 0)]
    [InlineData("TERMINATED", 0)]
    [InlineData("STOPPED", 3)]
    [InlineData("TERMINATED", 3)]
    public async Task A_VM_the_worker_stopped_counts_as_stopped_whatever_the_API_calls_it_and_however_long_it_takes(string stoppedStatus, int stoppingPolls)
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Stop).WithStopBehaviour(stoppingPolls, stoppedStatus);
        var repo = new InMemoryRunRepository();
        var jobId = $"job-ws-{stoppedStatus.ToLowerInvariant()}-{stoppingPolls}";

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request(jobId, Spec(jobId)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        result.FailureCode.ShouldBeNull();
        File.Exists(Track(repo, jobId)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_stop_that_never_lands_keeps_the_downloaded_results_and_records_that_the_VM_is_unverified()
    {
        var gcp = new FakeGcp().WithStopBehaviour(stoppingPolls: 1_000_000);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-slow", Spec("job-slow")), CancellationToken.None);

        File.Exists(Track(repo, "job-slow")).ShouldBeTrue("a good result is never thrown away over a lifecycle check (Hard Rule 14)");
        result.FinalPhase.ShouldBe(JobPhase.Completed);
        result.FailureCode.ShouldBe(RunErrorCodes.LifecycleUnverified);
        var row = LastRow(repo, "job-slow");
        row.Phase.ShouldBe(JobPhase.Completed);
        row.ErrorCode.ShouldBe(RunErrorCodes.LifecycleUnverified);
        row.OutputDir.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_results_are_on_disk_at_the_moment_the_VM_is_first_asked_to_stop()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var compute = new RecordingCompute(gcp);
        var runner = new CloudJobRunner(compute, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };
        compute.OnEndCall = () => compute.TrackThereAtFirstEndCall ??= File.Exists(Track(repo, "job-order"));

        var result = await runner.RunAsync(TestInputs.Request("job-order", Spec("job-order")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        compute.EndCalls.ShouldBeGreaterThan(0, "the runner never asked the VM to stop, so this test saw nothing");
        compute.TrackThereAtFirstEndCall.ShouldBe(true, "the VM was stopped before the results were safely on disk");
        (await VmStatusAsync(gcp, "job-order")).ShouldBe("STOPPED");
    }

    /// <summary>Delegates to the fake, but every look at a VM takes real time, as a slow network or a starved machine makes it.</summary>
    private sealed class SlowLookupCompute(IComputeGateway inner, TimeSpan lookupTime) : IComputeGateway
    {
        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public async Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            await Task.Delay(lookupTime, cancellationToken).ConfigureAwait(false);
            return await inner.GetVmAsync(vmName, zone, cancellationToken).ConfigureAwait(false);
        }

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);
    }

    /// <summary>Delegates to the fake and notes the instant of the first stop or delete request.</summary>
    private sealed class RecordingCompute(IComputeGateway inner) : IComputeGateway
    {
        public Action? OnEndCall { get; set; }

        public bool? TrackThereAtFirstEndCall { get; set; }

        public int EndCalls { get; private set; }

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            EndCalls++;
            OnEndCall?.Invoke();
            return inner.StopVmAsync(vmName, zone, cancellationToken);
        }

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            EndCalls++;
            OnEndCall?.Invoke();
            return inner.DeleteVmAsync(vmName, zone, cancellationToken);
        }

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);
    }

    // ---- Finding 3: a hung call, and a deadline the VM's own limit does not pre-empt ----

    [Fact]
    public async Task A_call_that_takes_real_time_is_not_cut_by_a_deadline_that_runs_on_test_time()
    {
        // MEASURED 2026-10-03 (#525): the call deadline ran on the wall clock, so a machine busy enough to stretch one 50 ms call
        // turned a healthy call into "request timed out". On the runner's clock a call that takes 150 real milliseconds is
        // still inside its 50 ms deadline, because test time did not move while it ran.
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var clocks = new Clocks();
        var runner = new CloudJobRunner(new SlowLookupCompute(gcp, TimeSpan.FromMilliseconds(150)), gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            BootTimeout = TimeSpan.FromMilliseconds(80),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            CallTimeout = TimeSpan.FromMilliseconds(50),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };

        var result = await runner.RunAsync(TestInputs.Request("job-slow-call", Spec("job-slow-call")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        result.FailureCode.ShouldBeNull();
    }

    [Fact]
    public async Task One_hung_call_does_not_hang_the_run()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = Runner(
            gcp,
            repo,
            (_, phase) =>
            {
                if (phase == JobPhase.Running)
                {
                    gcp.WithHungCalls(2);
                }
            },
            TimeSpan.FromSeconds(30),
            realTime: true);

        var finished = runner.RunAsync(TestInputs.Request("job-hang", Spec("job-hang")), CancellationToken.None);
        var winner = await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        winner.ShouldBeSameAs(finished, "a hung call was never cut");
        (await finished).FinalPhase.ShouldBe(JobPhase.Completed);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(240)]
    [InlineData(1440)]
    public void The_default_wait_ends_before_the_VMs_own_max_run_duration_so_result_timeout_is_reachable(int maxRunMinutes)
    {
        var maxRun = TimeSpan.FromMinutes(maxRunMinutes);

        var limit = CloudJobRunner.ResultWaitLimit(maxRun);

        limit.ShouldBeLessThan(maxRun, "the platform deletes the VM at maxRunDuration; waiting past it only ever sees a lost VM");
        limit.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    // ---- Finding 4: a boot failure reads as its own cause, not as a lost computer ----

    [Theory]
    [InlineData(FakeWorkerMode.GpuNotVisible, "gpu_not_visible")]
    [InlineData(FakeWorkerMode.ImagePullFailed, "image_pull_failed")]
    [InlineData(FakeWorkerMode.ManifestInvalid, "manifest_invalid")]
    [InlineData(FakeWorkerMode.WorkerCrash, "worker_crashed")]
    public async Task A_boot_failure_the_worker_wrote_to_status_json_is_recorded_under_its_own_code(FakeWorkerMode mode, string expectedCode)
    {
        var gcp = new FakeGcp().WithWorker(mode);
        var repo = new InMemoryRunRepository();
        var jobId = "job-boot-" + expectedCode.Replace('_', '-');

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request(jobId, Spec(jobId)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(expectedCode);
        LastRow(repo, jobId).ErrorCode.ShouldBe(expectedCode);
        (await VmStatusAsync(gcp, jobId)).ShouldNotBe("RUNNING", "a failed boot must not leave a VM billing (Hard Rule 11)");
    }

    // ---- Finding 12: the production composition never completes a run with simulated results ----

    [Fact]
    public async Task With_no_real_cloud_connected_a_run_fails_before_anything_is_uploaded_or_downloaded()
    {
        var gcp = new FakeGcp().WithCloudNotConnected();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-nc", Spec("job-nc"));

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.CloudNotConnected);
        gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldBeEmpty();
        gcp.CreateAttempts.ShouldBe(0);
        Directory.GetFileSystemEntries(request.OutputFolder).ShouldBeEmpty("no fake result may reach the user's output folder");
    }

    // ---- Finding 5: a resumed download skips provisioning ----

    [Fact]
    public async Task A_run_resumed_after_the_worker_finished_downloads_the_result_without_creating_a_second_VM()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete).WithDeleteOfMissingVmNotFound();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-rs", Spec("job-rs"), null, AfterTaskAction.Delete);
        await Runner(gcp, repo).RunAsync(request, CancellationToken.None);
        var firstFolder = LastRow(repo, "job-rs").OutputDir!;
        var createsBefore = gcp.CreateAttempts;

        // The app died after Downloading was recorded; the VM is long gone.
        await repo.UpsertAsync(LastRow(repo, "job-rs") with { Phase = JobPhase.Downloading, FinishedAt = null, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(1) }, CancellationToken.None);
        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreateAttempts.ShouldBe(createsBefore, "a finished job must never create another billed VM");
        LastRow(repo, "job-rs").OutputDir.ShouldBe(firstFolder);
        File.Exists(Path.Combine(firstFolder, "seq", "seq.bedgraph")).ShouldBeTrue();
    }
}
