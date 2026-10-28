using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #476, Hard Rule 11: a Keep-alive run's VM is held for the window and must then be STOPPED or gone. The worker's startup.sh
/// does that; the app verifies it, and when the VM is still up after the window plus a grace it ends the VM itself and records that.
/// Every wait here is on virtual time (FakeGcp plus RunnerClocks): a 30 minute window costs milliseconds.
/// </summary>
public class CloudJobRunnerKeepAliveEndTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static VmSpec Spec(string jobId) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "keep",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudJobRequest KeepRequest(string jobId, AfterKeepAliveAction after)
    {
        var request = TestInputs.Request(jobId, Spec(jobId), null, AfterTaskAction.KeepAlive);
        return request with { Options = request.Options with { KeepAliveMinutes = (int)Window.TotalMinutes, AfterKeepAlive = after } };
    }

    /// <summary>What the worker's own startup.sh does to the held VM: nothing, stop it, or delete it, once the window has run.</summary>
    private enum WorkerEnd
    {
        Never,
        Stops,
        Deletes,
    }

    private sealed class Rig
    {
        public required FakeGcp Gcp { get; init; }

        public required InMemoryRunRepository Repo { get; init; }

        public required CloudJobRunner Runner { get; init; }

        public required RunnerClocks Clocks { get; init; }

        /// <summary>The VM's status at every look the runner took after the run was recorded Completed, with the time since then.</summary>
        public List<(TimeSpan Since, string? Status)> Seen { get; } = [];

        public long? CompletedAt { get; set; }

        public RunRecord Row(string jobId) => Repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

        public async Task<string?> StatusAsync(string jobId) => (await Gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;
    }

    private static Rig MakeRig(string jobId, WorkerEnd workerEnd, Func<FakeGcp, FakeGcp>? tweak = null, Action<Rig>? onCompleted = null)
    {
        var gcp = tweak is null ? new FakeGcp() : tweak(new FakeGcp());
        var repo = new InMemoryRunRepository();
        var clocks = new RunnerClocks();
        clocks.ParkPump(gcp, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
        Rig? rig = null;
        var ended = false;
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo, (_, phase) =>
        {
            if (phase == JobPhase.Completed)
            {
                rig!.CompletedAt = clocks.Run.GetTimestamp();
                onCompleted?.Invoke(rig);
            }
        })
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(80),
            CallTimeout = TimeSpan.FromSeconds(10),
            CreateTimeout = TimeSpan.FromSeconds(10),
            CreateSettleTimeout = TimeSpan.FromSeconds(2),
            UploadTimeout = TimeSpan.FromSeconds(10),
            BootTimeout = TimeSpan.FromSeconds(10),
            LifecycleTimeout = TimeSpan.FromSeconds(10),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            KeepAliveGrace = Grace,
            KeepAlivePollInterval = TimeSpan.FromMinutes(1),
            TimeProvider = clocks.Run,
            PollDelay = async (span, cancellationToken) =>
            {
                await clocks.SleepAsync(span, cancellationToken);
                if (rig!.CompletedAt is not { } start)
                {
                    return;
                }

                var since = clocks.Run.GetElapsedTime(start);
                rig.Seen.Add((since, await rig.StatusAsync(jobId)));
                if (!ended && since >= Window && workerEnd != WorkerEnd.Never)
                {
                    ended = true;
                    if (workerEnd == WorkerEnd.Stops)
                    {
                        await gcp.StopVmAsync($"deg-{jobId}", Zone, CancellationToken.None);
                    }
                    else
                    {
                        await gcp.DeleteVmAsync($"deg-{jobId}", Zone, CancellationToken.None);
                    }
                }
            },
        };
        rig = new Rig { Gcp = gcp, Repo = repo, Runner = runner, Clocks = clocks };
        return rig;
    }

    [Fact]
    public async Task A_kept_VM_the_worker_stopped_after_the_window_is_confirmed_and_left_alone()
    {
        var rig = MakeRig("job-ka1", WorkerEnd.Stops);

        var result = await rig.Runner.RunAsync(KeepRequest("job-ka1", AfterKeepAliveAction.Stop), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        result.FailureCode.ShouldBeNull("the worker did its own end; the app had nothing to record");
        rig.Row("job-ka1").ErrorCode.ShouldBeNull();
        (await rig.StatusAsync("job-ka1")).ShouldBe("STOPPED");
        rig.Seen.Count.ShouldBeGreaterThan(5, "the window was never watched (vacuous)");
        rig.Seen.Where(s => s.Since < Window).ShouldAllBe(s => s.Status == "RUNNING", "the VM was ended inside the window the user paid for");
    }

    [Fact]
    public async Task A_kept_VM_the_worker_deleted_after_the_window_is_confirmed_gone()
    {
        var rig = MakeRig("job-ka2", WorkerEnd.Deletes);

        var result = await rig.Runner.RunAsync(KeepRequest("job-ka2", AfterKeepAliveAction.Delete), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        result.FailureCode.ShouldBeNull();
        (await rig.StatusAsync("job-ka2")).ShouldBeNull();
    }

    [Fact]
    public async Task A_kept_VM_still_running_after_the_window_and_grace_is_stopped_by_the_app_and_the_row_says_so()
    {
        var rig = MakeRig("job-ka3", WorkerEnd.Never);

        var result = await rig.Runner.RunAsync(KeepRequest("job-ka3", AfterKeepAliveAction.Stop), CancellationToken.None);

        (await rig.StatusAsync("job-ka3")).ShouldBe("STOPPED", "Hard Rule 11: a keep-alive VM left RUNNING past its window keeps billing");
        result.FinalPhase.ShouldBe(JobPhase.Completed, "the results were saved; only the VM end needed the app");
        result.FailureCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
        var row = rig.Row("job-ka3");
        row.Phase.ShouldBe(JobPhase.Completed);
        row.ErrorCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
        rig.Seen.ShouldAllBe(s => s.Status == "RUNNING", "the app ended the VM before the window plus grace was over");
        rig.Seen.Max(s => s.Since).ShouldBeGreaterThanOrEqualTo(Window + Grace);
    }

    [Fact]
    public async Task A_kept_VM_still_running_after_the_window_with_Delete_afterwards_is_deleted_by_the_app()
    {
        var rig = MakeRig("job-ka4", WorkerEnd.Never);

        var result = await rig.Runner.RunAsync(KeepRequest("job-ka4", AfterKeepAliveAction.Delete), CancellationToken.None);

        (await rig.StatusAsync("job-ka4")).ShouldBeNull();
        result.FailureCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
        rig.Row("job-ka4").ErrorCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
    }

    [Fact]
    public async Task A_VM_the_worker_only_stopped_when_Delete_was_chosen_afterwards_is_deleted_by_the_app()
    {
        var rig = MakeRig("job-ka5", WorkerEnd.Stops);

        var result = await rig.Runner.RunAsync(KeepRequest("job-ka5", AfterKeepAliveAction.Delete), CancellationToken.None);

        (await rig.StatusAsync("job-ka5")).ShouldBeNull("a stopped disk still bills, and the user chose delete");
        result.FailureCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
    }

    [Fact]
    public async Task A_VM_the_app_cannot_end_is_recorded_unverified_with_the_Cloud_page_action()
    {
        var rig = MakeRig("job-ka6", WorkerEnd.Never, g => g.WithSilentNoOpDeletes());

        var result = await rig.Runner.RunAsync(KeepRequest("job-ka6", AfterKeepAliveAction.Delete), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        result.FailureCode.ShouldBe(RunErrorCodes.LifecycleUnverified);
        rig.Row("job-ka6").ErrorCode.ShouldBe(RunErrorCodes.LifecycleUnverified);
        (await rig.StatusAsync("job-ka6")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Closing_the_wait_early_leaves_the_VM_and_the_finished_result_alone_for_the_reconciler()
    {
        using var cts = new CancellationTokenSource();
        var rig = MakeRig("job-ka7", WorkerEnd.Never, onCompleted: _ => cts.Cancel());

        var result = await rig.Runner.RunAsync(KeepRequest("job-ka7", AfterKeepAliveAction.Stop), cts.Token);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        result.FailureCode.ShouldBeNull();
        (await rig.StatusAsync("job-ka7")).ShouldBe("RUNNING", "the app closed or the run was cancelled: JobReconciler enforces the expiry next start");
        rig.Row("job-ka7").Phase.ShouldBe(JobPhase.Completed);
    }

    [Fact]
    public async Task A_Stop_run_is_not_held_waiting_for_any_window()
    {
        var rig = MakeRig("job-ka8", WorkerEnd.Never);
        var request = TestInputs.Request("job-ka8", Spec("job-ka8") with { Lifecycle = "stop" }, null, AfterTaskAction.Stop);

        var result = await rig.Runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        rig.Seen.ShouldBeEmpty("only a keep-alive run waits out a window");
        (await rig.StatusAsync("job-ka8")).ShouldBe("STOPPED");
    }
}
