using System.Runtime.CompilerServices;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Framing-free review of the runner (round 4): every failure once a VM may exist ends the VM or records
/// vm_end_unconfirmed, every run ends in one recorded terminal state, and the returned result agrees with the row.
/// Each test is written from the symptom (a VM left RUNNING, a row stuck in Cancelling, a result that disagrees with
/// its row), never from the fix.
/// </summary>
public class CloudJobRunnerGuaranteesTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";
    private const string OtherZone = "us-central1-b";

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

    private static CloudJobRunner Runner(
        IComputeGateway compute,
        FakeGcp gcp,
        IRunRepository repo,
        Action<string, JobPhase>? onPhase = null)
        => new(compute, gcp, gcp, gcp, repo, onPhase)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            CallTimeout = TimeSpan.FromSeconds(10),
            CreateTimeout = TimeSpan.FromSeconds(10),
            CreateSettleTimeout = TimeSpan.FromSeconds(2),
            UploadTimeout = TimeSpan.FromSeconds(10),
            BootTimeout = TimeSpan.FromSeconds(10),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId, string zone = Zone)
        => (await gcp.GetVmAsync($"deg-{jobId}", zone, CancellationToken.None))?.Status;

    private static async Task<CloudJobRequest> SeedAsync(FakeGcp gcp, InMemoryRunRepository repo, string jobId, JobPhase phase, string zone = Zone, IReadOnlyList<string>? zones = null, TimeSpan? maxRun = null)
    {
        var request = TestInputs.Request(jobId, Spec(jobId, maxRun), zones);
        await gcp.CreateVmAsync(request.Spec, zone, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord(jobId, phase, DateTimeOffset.UtcNow), CancellationToken.None);
        return request;
    }

    /// <summary>A compute gateway whose calls a test can override one by one; the rest go to the fake.</summary>
    private sealed class ScriptedCompute(FakeGcp inner) : IComputeGateway
    {
        public Func<VmSpec, string, CancellationToken, Task<VmDescriptor>>? OnCreate { get; set; }

        public Func<string, string, CancellationToken, Task<VmDescriptor?>>? OnGet { get; set; }

        public Func<string, string, CancellationToken, Task>? OnDelete { get; set; }

        public Func<string, CancellationToken, Task<IReadOnlyList<VmDescriptor>>>? OnFind { get; set; }

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
            => OnCreate is null ? inner.CreateVmAsync(spec, zone, cancellationToken) : OnCreate(spec, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
            => OnGet is null ? inner.GetVmAsync(vmName, zone, cancellationToken) : OnGet(vmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
            => OnDelete is null ? inner.DeleteVmAsync(vmName, zone, cancellationToken) : OnDelete(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
            => OnFind is null ? inner.FindByJobIdAsync(jobId, cancellationToken) : OnFind(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken)
            => inner.ListByInstallationAsync(installationId, cancellationToken);
    }

    // ---- M1: a gateway's own deadline (an OCE with the caller's token live) is not the caller cancelling ----

    [Fact]
    public async Task A_gateway_deadline_cancellation_during_create_still_ends_the_VM_the_insert_made()
    {
        // The VM is still PROVISIONING when the failure lands, and a stop of an instance that is not RUNNING is rejected.
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never).WithBootStatusPolls(5).WithStopRejectedUnlessRunning();
        var compute = new ScriptedCompute(gcp)
        {
            OnCreate = async (spec, zone, token) =>
            {
                await gcp.CreateVmAsync(spec, zone, token);
                throw new TaskCanceledException("the gateway's own HTTP deadline");
            },
        };
        var repo = new InMemoryRunRepository();

        var result = await Runner(compute, gcp, repo).RunAsync(TestInputs.Request("job-m1", Spec("job-m1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        (await gcp.FindByJobIdAsync("job-m1", CancellationToken.None)).ShouldBeEmpty("the insert landed and nothing ended it: a VM billing under a Failed record");
    }

    // ---- M2: a create that fails after the server accepted it ----

    [Fact]
    public async Task A_create_whose_response_is_lost_to_a_network_failure_is_swept_before_the_run_is_recorded()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var compute = new ScriptedCompute(gcp)
        {
            OnCreate = async (spec, zone, token) =>
            {
                await gcp.CreateVmAsync(spec, zone, token);
                throw new CloudOperationException(new CloudError(null, null, "connection reset by peer"), CloudErrorKind.Network);
            },
        };
        var repo = new InMemoryRunRepository();

        var result = await Runner(compute, gcp, repo).RunAsync(TestInputs.Request("job-m2a", Spec("job-m2a")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        (await gcp.FindByJobIdAsync("job-m2a", CancellationToken.None)).ShouldBeEmpty("the accepted insert was left alive");
    }

    [Fact]
    public async Task A_zone_whose_create_failed_after_landing_leaves_no_second_VM_when_the_next_zone_succeeds()
    {
        var gcp = new FakeGcp();
        var compute = new ScriptedCompute(gcp)
        {
            OnCreate = async (spec, zone, token) =>
            {
                if (zone == Zone)
                {
                    await gcp.CreateVmAsync(spec, zone, token);
                    throw new CloudOperationException(new CloudError("INTERNAL", 500, "internal error"), CloudErrorKind.Other);
                }

                return await gcp.CreateVmAsync(spec, zone, token);
            },
        };
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-m2b", Spec("job-m2b"), [Zone, OtherZone]);

        var result = await Runner(compute, gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var owned = await gcp.FindByJobIdAsync("job-m2b", CancellationToken.None);
        owned.Select(v => v.Zone).ShouldBe([OtherZone], "the first zone's VM landed and was never looked for: two VMs billing for one job");
    }

    // ---- M3: cancel ----

    [Fact]
    public async Task A_cancel_whose_delete_loses_a_race_with_the_worker_deleting_itself_is_Cancelled()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedAsync(gcp, repo, "job-m3a", JobPhase.Running);
        gcp.WithNextDeleteRacingAWorkerDelete();

        await Runner(gcp, gcp, repo).CancelAsync("job-m3a", CancellationToken.None);

        LastRow(repo, "job-m3a").Phase.ShouldBe(JobPhase.Cancelled, LastRow(repo, "job-m3a").ErrorDetail);
        (await gcp.FindByJobIdAsync("job-m3a", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancel_whose_VM_is_still_there_after_the_delete_is_not_recorded_Cancelled()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var compute = new ScriptedCompute(gcp) { OnDelete = (_, _, _) => Task.CompletedTask };
        var repo = new InMemoryRunRepository();
        await SeedAsync(gcp, repo, "job-m3b", JobPhase.Running);

        await Runner(compute, gcp, repo).CancelAsync("job-m3b", CancellationToken.None);

        var row = LastRow(repo, "job-m3b");
        row.Phase.ShouldBe(JobPhase.Failed, "the delete returned but the VM is still alive: Cancelled would be a lie");
        row.ErrorCode.ShouldBe(RunErrorCodes.CancelFailed);
    }

    [Fact]
    public async Task A_cancel_whose_caller_token_is_cancelled_still_records_a_terminal_state()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedAsync(gcp, repo, "job-m3c", JobPhase.Running);
        using var cts = new CancellationTokenSource();
        var findCalls = 0;
        var compute = new ScriptedCompute(gcp)
        {
            // The caller gives up while the first look is in flight; every later look answers normally.
            OnFind = async (jobId, token) =>
            {
                if (Interlocked.Increment(ref findCalls) == 1)
                {
                    await cts.CancelAsync();
                    await Task.Delay(Timeout.Infinite, token);
                }

                return await gcp.FindByJobIdAsync(jobId, token);
            },
        };

        await Runner(compute, gcp, repo).CancelAsync("job-m3c", cts.Token);

        JobStateMachine.IsTerminal(LastRow(repo, "job-m3c").Phase).ShouldBeTrue($"the row was left in {LastRow(repo, "job-m3c").Phase}");
        (await gcp.FindByJobIdAsync("job-m3c", CancellationToken.None)).ShouldBeEmpty("a cancel that gave up halfway left the VM billing");
    }

    [Fact]
    public async Task A_second_cancel_that_fails_after_the_first_recorded_Cancelled_leaves_the_row_Cancelled_and_throws_nothing()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedAsync(gcp, repo, "job-m3d", JobPhase.Running);

        var findCalls = 0;
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compute = new ScriptedCompute(gcp)
        {
            OnFind = async (jobId, token) =>
            {
                var call = Interlocked.Increment(ref findCalls);
                if (call == 1)
                {
                    await releaseA.Task;
                }
                else if (call == 2)
                {
                    bEntered.TrySetResult();
                    await aDone.Task;
                    throw new CloudOperationException(new CloudError(null, null, "connection reset"), CloudErrorKind.Network);
                }

                return await gcp.FindByJobIdAsync(jobId, token);
            },
        };
        var runner = Runner(compute, gcp, repo);

        var a = runner.CancelAsync("job-m3d", CancellationToken.None);
        var b = runner.CancelAsync("job-m3d", CancellationToken.None);
        await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        releaseA.SetResult();
        await a.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        aDone.SetResult();
        await b.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        LastRow(repo, "job-m3d").Phase.ShouldBe(JobPhase.Cancelled);
    }

    // ---- M4: the result deadline is measured from the VM's creation ----

    [Fact]
    public async Task A_boot_longer_than_the_margin_does_not_let_the_platform_delete_the_VM_before_the_result_deadline()
    {
        // Virtual time: every look at the VM advances the clock by 1 s, so no real timing is involved. The VM's
        // maxRunDuration is 11 s, the default wait limit 5.5 s, and the VM reads RUNNING after 6 looks (6 s). Waiting 5.5 s
        // from Running would run past the platform's deletion at 11 s (vm_unhealthy); measured from creation the deadline
        // has already passed at the first look and the runner ends the VM itself.
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never).WithBootStatusPolls(5).WithMaxRunDurationEnforced();
        var compute = new ScriptedCompute(gcp)
        {
            OnGet = (name, zone, token) =>
            {
                time.Advance(TimeSpan.FromSeconds(1));
                return gcp.GetVmAsync(name, zone, token);
            },
        };
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(compute, gcp, gcp, gcp, repo)
        {
            TimeProvider = time,
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            BootTimeout = TimeSpan.FromSeconds(30),
            LifecycleTimeout = TimeSpan.FromSeconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = await runner.RunAsync(TestInputs.Request("job-m4", Spec("job-m4", TimeSpan.FromSeconds(11))), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.ResultTimeout, result.FailureMessage);
    }

    // ---- M5: a VM from an earlier runner, in a zone that is not the ladder's first, is adopted ----

    [Fact]
    public async Task A_resumed_run_adopts_a_VM_in_a_later_zone_and_completes_when_the_worker_finishes()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedAsync(gcp, repo, "job-m5", JobPhase.Provisioning, OtherZone, [Zone, OtherZone]);
        var bucket = FakeGcp.BucketName(Project);
        gcp.PutObject(bucket, "jobs/job-m5/manifest.json", """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray());
        var attemptsBefore = gcp.CreateAttempts;
        var runner = Runner(gcp, gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Running)
            {
                gcp.SimulateWorkerFinishing(request.Spec, OtherZone);
            }
        });

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreateAttempts.ShouldBe(attemptsBefore, "the runner created a second VM instead of adopting the one that exists");
        (await gcp.FindByJobIdAsync("job-m5", CancellationToken.None)).Select(v => v.Zone).ShouldBe([OtherZone]);
    }

    // ---- M6: a notification callback never changes the run's outcome ----

    [Fact]
    public async Task A_callback_that_throws_on_Completed_does_not_end_a_kept_alive_VM_or_change_the_result()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Completed)
            {
                throw new InvalidOperationException("the UI is gone");
            }
        });

        var result = await runner.RunAsync(TestInputs.Request("job-m6", Spec("job-m6"), null, AfterTaskAction.KeepAlive), CancellationToken.None);

        LastRow(repo, "job-m6").Phase.ShouldBe(JobPhase.Completed);
        result.FinalPhase.ShouldBe(LastRow(repo, "job-m6").Phase, "the result and the persisted row disagree");
        (await VmStatusAsync(gcp, "job-m6")).ShouldBe("RUNNING", "keep-alive was chosen and a callback failure ended the VM");
    }

    [Fact]
    public async Task A_callback_that_throws_on_every_phase_still_lets_the_run_complete()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, gcp, repo, (_, _) => throw new InvalidOperationException("the UI is gone"));

        var result = await runner.RunAsync(TestInputs.Request("job-m6b", Spec("job-m6b")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        LastRow(repo, "job-m6b").Phase.ShouldBe(JobPhase.Completed);
    }

    [Fact]
    public async Task A_run_that_fails_after_another_writer_recorded_a_terminal_state_returns_that_state()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, gcp, repo, (jobId, phase) =>
        {
            if (phase == JobPhase.Running)
            {
                // Another writer (a cancel from a second runner instance) records Cancelled while this run waits.
                repo.UpsertAsync(LastRow(repo, jobId) with { Phase = JobPhase.Cancelled }, CancellationToken.None).GetAwaiter().GetResult();
            }
        });

        var result = await runner.RunAsync(TestInputs.Request("job-m6c", Spec("job-m6c")), CancellationToken.None);

        LastRow(repo, "job-m6c").Phase.ShouldBe(JobPhase.Cancelled);
        result.FinalPhase.ShouldBe(JobPhase.Cancelled, "the result says Failed while the persisted row says Cancelled");
    }

    // ---- Round 5 ----

    [Fact]
    public async Task A_permission_failure_while_the_VM_is_still_booting_deletes_it_because_a_stop_would_be_rejected()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never).WithBootStatusPolls(5).WithStopRejectedUnlessRunning();
        gcp.WithGetVmFailure(new CloudError("IAM_PERMISSION_DENIED", 403, "Required 'compute.instances.get' permission."), 1);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-r5a", Spec("job-r5a")), CancellationToken.None);

        result.FailureCode.ShouldBe("permission", "the stop was rejected, so the VM end read as unconfirmed");
        (await gcp.FindByJobIdAsync("job-r5a", CancellationToken.None)).ShouldBeEmpty("a booting VM was left billing");
    }

    private sealed class ThrowingOnGetAllRepository(InMemoryRunRepository inner, int throwOnCall) : IRunRepository
    {
        private int _calls;

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
            => Interlocked.Increment(ref _calls) == throwOnCall ? throw new InvalidOperationException("database is busy") : inner.GetAllAsync(cancellationToken);

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken) => inner.UpsertAsync(run, cancellationToken);
    }

    [Fact]
    public async Task A_busy_repository_read_inside_a_resumed_cancel_never_records_Failed_other_without_looking_at_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var inner = new InMemoryRunRepository();
        var request = await SeedAsync(gcp, inner, "job-r5b", JobPhase.Cancelling);
        // Call 1 is RunCoreAsync's own phase read; call 2 is the first read inside CancelAsync.
        var repo = new ThrowingOnGetAllRepository(inner, throwOnCall: 2);

        var result = await Runner(gcp, gcp, repo).RunAsync(request, CancellationToken.None);

        (await gcp.FindByJobIdAsync("job-r5b", CancellationToken.None)).ShouldBeEmpty("the cancel never looked at the VM");
        LastRow(inner, "job-r5b").Phase.ShouldBe(JobPhase.Cancelled, LastRow(inner, "job-r5b").ErrorDetail);
        result.FinalPhase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task Running_again_a_row_already_Failed_returns_its_error_code()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-r5c", JobPhase.Failed, DateTimeOffset.UtcNow) { ErrorCode = RunErrorCodes.ModelOom, ErrorDetail = "detail" }, CancellationToken.None);

        var result = await Runner(gcp, gcp, repo).RunAsync(TestInputs.Request("job-r5c", Spec("job-r5c")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.ModelOom, "a terminal row's code was dropped from the result");
    }

    [Fact]
    public async Task A_cancel_whose_first_delete_fails_still_tries_every_other_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedAsync(gcp, repo, "job-r5d", JobPhase.Running);
        await gcp.CreateVmAsync(request.Spec, OtherZone, CancellationToken.None);
        var deletes = 0;
        var compute = new ScriptedCompute(gcp)
        {
            OnDelete = (name, zone, token) => Interlocked.Increment(ref deletes) == 1
                ? throw new CloudOperationException(new CloudError(null, null, "connection reset"), CloudErrorKind.Network)
                : gcp.DeleteVmAsync(name, zone, token),
        };

        await Runner(compute, gcp, repo).CancelAsync("job-r5d", CancellationToken.None);

        (await gcp.FindByJobIdAsync("job-r5d", CancellationToken.None)).Count.ShouldBe(1, "the first failed delete stopped the cancel, leaving the second VM billing too");
        LastRow(repo, "job-r5d").ErrorCode.ShouldBe(RunErrorCodes.CancelFailed);
    }

    [Fact]
    public async Task A_VM_found_by_label_is_ended_under_its_own_name_not_the_specs()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedAsync(gcp, repo, "job-r5e", JobPhase.Running);
        gcp.WithAlreadyExists("deg-job-r5e-stray", OtherZone);
        var compute = new ScriptedCompute(gcp)
        {
            OnFind = async (jobId, token) => [.. await gcp.FindByJobIdAsync(jobId, token), new VmDescriptor("deg-job-r5e-stray", OtherZone, "RUNNING")],
        };
        gcp.WithBillingOff(Project);

        await Runner(compute, gcp, repo).RunAsync(request, CancellationToken.None);

        (await gcp.GetVmAsync("deg-job-r5e-stray", OtherZone, CancellationToken.None))!.Status.ShouldBe("STOPPED", "the end looked up the spec's name in that zone, found nothing, and called it done");
    }

    // ---- Low (a): the end helpers never throw ----

    [Fact]
    public async Task A_VM_end_that_hits_a_non_cloud_exception_is_recorded_as_unconfirmed_not_thrown()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed);
        var broken = false;
        var compute = new ScriptedCompute(gcp)
        {
            OnGet = (name, zone, token) => broken ? throw new InvalidOperationException("a bug under the gateway") : gcp.GetVmAsync(name, zone, token),
        };
        var repo = new InMemoryRunRepository();
        var runner = Runner(compute, gcp, repo, (_, phase) => broken |= phase == JobPhase.Running);

        var result = await runner.RunAsync(TestInputs.Request("job-la", Spec("job-la")), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed, "the VM end could not be confirmed and the record does not say so");
    }

    [Fact]
    public async Task A_lookup_by_label_that_hits_a_non_cloud_exception_is_recorded_as_unconfirmed_not_thrown()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedAsync(gcp, repo, "job-lb", JobPhase.Running);
        var compute = new ScriptedCompute(gcp) { OnFind = (_, _) => throw new InvalidOperationException("a bug under the gateway") };

        var result = await Runner(compute, gcp, repo).RunAsync(request, CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed);
    }

    // ---- Low (b): the lifecycle_unverified copy says what the code means ----

    [Fact]
    public void The_lifecycle_unverified_copy_does_not_claim_the_VM_failed_to_stop()
    {
        var text = ResourceText("RunError_lifecycle_unverified");

        text.ShouldNotContain("did not stop", Case.Insensitive, "the code means the stop could not be confirmed");
        text.ShouldContain("confirm", Case.Insensitive);
        text.ShouldNotContain("—");
    }

    private static string ResourceText(string key, [CallerFilePath] string here = "")
    {
        // Found from this source file, so it works under any --artifacts-path (the fixture walk from the binary does not, #495).
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Resources.resw was not found above the test source");
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(dir.FullName, "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw"));
        return xml.Root!.Elements("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    // ---- Low (c): a worker contract violation is not "free up space" ----

    [Fact]
    public async Task A_result_that_lists_a_file_missing_from_the_bucket_is_a_worker_failure_not_a_download_failure()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-lc", Spec("job-lc"));
        var runner = Runner(gcp, gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Running)
            {
                gcp.PutObject(
                    FakeGcp.BucketName(Project),
                    "jobs/job-lc/result.json",
                    """{"schema":1,"jobId":"job-lc","status":"done","inputs":[{"id":"in1","status":"done","outputs":["output/seq/seq.bedgraph"],"files":[{"path":"output/seq/seq.bedgraph","sha256":"00","bytes":3}],"notices":[],"stats":null,"error":null}],"error":null}"""u8.ToArray());
            }
        });

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.WorkerFailed);
    }

    // ---- Low (d): a resume of a Cancelling row finishes the cancel ----

    [Fact]
    public async Task Resuming_a_run_that_was_Cancelling_finishes_the_cancel_and_deletes_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var request = await SeedAsync(gcp, repo, "job-ld", JobPhase.Cancelling);

        var result = await Runner(gcp, gcp, repo).RunAsync(request, CancellationToken.None);

        LastRow(repo, "job-ld").Phase.ShouldBe(JobPhase.Cancelled, LastRow(repo, "job-ld").ErrorDetail);
        result.FinalPhase.ShouldBe(JobPhase.Cancelled);
        (await gcp.FindByJobIdAsync("job-ld", CancellationToken.None)).ShouldBeEmpty();
    }
}
