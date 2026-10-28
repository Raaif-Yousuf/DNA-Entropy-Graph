using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #476, Hard Rule 11: a Keep-alive VM must be STOPPED or gone once its window has run. startup.sh ends it itself; the app
/// verifies it while it is open through <see cref="JobReconciler.EnforceKeepAliveAsync"/> (run every few minutes by
/// <see cref="KeepAliveSweeper"/>), which finds the VM by its job-id label (never by a remembered name, so a VM another run has
/// since reused is never touched), ends it past the window plus a grace, and notes it on the finished row without overwriting a note
/// the row already has. The run itself holds nothing while the window runs: a Completed run is not an active run.
/// DECISION (agent-made, 2026-10-28, reversible): the sweep replaces the run's own long in-run wait, because that wait kept a
/// Completed run in ActiveRuns for up to four hours (blocking updates and the reconciler).
/// </summary>
public class JobReconcilerKeepAliveSweepTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";
    private static readonly DateTimeOffset Launch = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig
    {
        public Rig()
        {
            Clock = new MutableClock(Launch);
            Env = new JobReconcilerTests.Env(new FakeGcp(Clock).WithWorker(FakeWorkerMode.Never));
        }

        public MutableClock Clock { get; }

        public JobReconcilerTests.Env Env { get; }

        public FakeGcp Gcp => Env.Gcp;

        public JobReconciler Reconciler() => Env.Reconciler(Clock);

        public void AdvanceMinutes(int minutes) => Clock.Advance(TimeSpan.FromMinutes(minutes));

        /// <summary>A Completed Keep-alive run that finished at launch (30 minute window) whose VM is up. The clock is advanced by each test.</summary>
        public async Task<CloudJobRequest> SeedAsync(string jobId, AfterKeepAliveAction after, JobPhase phase = JobPhase.Completed, Func<RunRecord, RunRecord>? tweak = null)
            => await Env.SeedAsync(
                jobId,
                phase,
                vm: true,
                after: AfterTaskAction.KeepAlive,
                afterKeepAlive: after,
                keepAliveMinutes: 30,
                tweak: row => (tweak ?? (r => r))(row with { FinishedAt = Launch }));

        public async Task<string?> StatusAsync(string jobId) => (await Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).SingleOrDefault()?.Status;
    }

    private static VmSpec SpecOf(string jobId) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    [Theory]
    [InlineData(29)]
    [InlineData(34)]
    public async Task A_VM_inside_its_window_or_the_grace_after_it_is_left_running(int minutesAfterFinish)
    {
        var rig = new Rig();
        await rig.SeedAsync("job-w", AfterKeepAliveAction.Stop);
        rig.Clock.Advance(TimeSpan.FromMinutes(minutesAfterFinish));

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-w")).ShouldBe("RUNNING", "the worker's own end gets the window plus the grace first");
        outcomes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AfterKeepAliveAction.Stop, "STOPPED")]
    [InlineData(AfterKeepAliveAction.Delete, null)]
    public async Task A_VM_still_running_after_the_window_and_grace_is_ended_by_the_app_and_the_finished_row_says_so(AfterKeepAliveAction after, string? expected)
    {
        var rig = new Rig();
        await rig.SeedAsync("job-e", after);
        rig.Clock.Advance(TimeSpan.FromMinutes(36));

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-e")).ShouldBe(expected, "Hard Rule 11: a Keep-alive VM past its window keeps billing");
        outcomes.Count.ShouldBe(1);
        outcomes[0].Action.ShouldBe(after == AfterKeepAliveAction.Delete ? LifecycleAction.VmDeleted : LifecycleAction.VmStopped);
        var row = rig.Env.Row("job-e");
        row.Phase.ShouldBe(JobPhase.Completed, "the results were saved; the note does not change the phase");
        row.ErrorCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
    }

    [Fact]
    public async Task A_VM_the_worker_already_stopped_is_left_alone_and_nothing_is_recorded()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-s", AfterKeepAliveAction.Stop);
        var vm = (await rig.Gcp.FindByJobIdAsync("job-s", CancellationToken.None)).Single();
        await rig.Gcp.StopVmAsync(vm.Name, vm.Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromMinutes(40));

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty();
        rig.Env.Row("job-s").ErrorCode.ShouldBeNull("the worker did its own end: nothing for the app to note");
    }

    [Fact]
    public async Task A_VM_the_worker_already_deleted_records_nothing()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-d", AfterKeepAliveAction.Delete);
        var vm = (await rig.Gcp.FindByJobIdAsync("job-d", CancellationToken.None)).Single();
        await rig.Gcp.DeleteVmAsync(vm.Name, vm.Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromMinutes(40));

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty();
        rig.Env.Row("job-d").ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task An_earlier_note_on_the_row_is_not_overwritten_by_the_keep_alive_note()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-n", AfterKeepAliveAction.Stop, tweak: r => r with { ErrorCode = RunErrorCodes.WorkerDefaultAccount, ErrorDetail = "kept-detail" });
        rig.AdvanceMinutes(36);

        await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-n")).ShouldBe("STOPPED", "the VM is still ended");
        var row = rig.Env.Row("job-n");
        row.ErrorCode.ShouldBe(RunErrorCodes.WorkerDefaultAccount, "the earlier note is the one the Runs page shows; one note per row");
        row.ErrorDetail.ShouldBe("kept-detail");
    }

    [Fact]
    public async Task A_VM_another_run_reused_after_the_user_stopped_it_is_never_ended_by_the_first_runs_window()
    {
        // H1 (cold review of 547568af): run A kept its VM; the user stopped it; run B reused it (VmProvisioner.TryReuseAsync relabels
        // the VM to B's job id and starts it). A's expiry must find nothing under A's job id and leave B's VM running.
        var rig = new Rig();
        await rig.SeedAsync("job-a", AfterKeepAliveAction.Stop);
        var vm = (await rig.Gcp.FindByJobIdAsync("job-a", CancellationToken.None)).Single();
        await rig.Gcp.StopVmAsync(vm.Name, vm.Zone, CancellationToken.None);
        await rig.Gcp.ReuseVmAsync(SpecOf("job-b"), vm.Name, vm.Zone, CancellationToken.None);
        (await rig.Gcp.GetVmAsync(vm.Name, vm.Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
        rig.AdvanceMinutes(40);

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.Gcp.GetVmAsync(vm.Name, vm.Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING", "that VM is run B's now");
        outcomes.ShouldBeEmpty();
        rig.Env.Row("job-a").ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task A_run_that_did_not_complete_is_not_this_sweeps_business()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-f", AfterKeepAliveAction.Stop, JobPhase.Failed);
        rig.AdvanceMinutes(40);

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty("a failed run's VM is applied at once by the launch pass, not by the window sweep");
    }

    [Fact]
    public async Task A_VM_far_past_its_window_is_left_to_the_launch_pass_and_the_idle_sweep()
    {
        // Bounds the cost of a tick: only rows whose window ended within a day are looked up.
        var rig = new Rig();
        await rig.SeedAsync("job-old", AfterKeepAliveAction.Stop);
        rig.Clock.Advance(TimeSpan.FromDays(2));

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty();
        (await rig.StatusAsync("job-old")).ShouldBe("RUNNING");
    }

    [Fact]
    public async Task A_job_something_else_drives_is_skipped_not_double_driven()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-busy", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);
        var release = new TaskCompletionSource();
        var driver = rig.Env.Active.TryStart("job-busy", async _ => await release.Task);
        (driver is null).ShouldBeFalse();

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty();
        (await rig.StatusAsync("job-busy")).ShouldBe("RUNNING");
        release.SetResult();
        await driver!;
    }

    // ---- the periodic trigger ----

    private sealed class CapturingTimeProvider : TimeProvider
    {
        public List<(TimerCallback Callback, TimeSpan Due, TimeSpan Period, FakeTimer Timer)> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer();
            Timers.Add((_ => callback(state), dueTime, period, timer));
            return timer;
        }
    }

    private sealed class FakeTimer : ITimer
    {
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_sweeper_runs_the_sweep_on_every_tick_and_never_two_at_once()
    {
        var time = new CapturingTimeProvider();
        var gate = new TaskCompletionSource();
        var calls = 0;
        using var sweeper = new KeepAliveSweeper(async _ => { calls++; await gate.Task; }, time);

        sweeper.Start();
        time.Timers.Count.ShouldBe(1);
        time.Timers[0].Period.ShouldBe(KeepAliveSweeper.DefaultPeriod);
        time.Timers[0].Callback(null);
        time.Timers[0].Callback(null);
        calls.ShouldBe(1, "a slow sweep must not stack up behind the next tick");
        gate.SetResult();
        await sweeper.LastTickAsync;
        time.Timers[0].Callback(null);
        await sweeper.LastTickAsync;
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_failing_sweep_is_logged_by_class_and_the_next_tick_still_runs()
    {
        var time = new CapturingTimeProvider();
        var log = new JobReconcilerTests.RecordingLog();
        var calls = 0;
        using var sweeper = new KeepAliveSweeper(_ => { calls++; throw new TimeoutException("secret detail"); }, time, log);

        sweeper.Start();
        time.Timers[0].Callback(null);
        await sweeper.LastTickAsync;
        time.Timers[0].Callback(null);
        await sweeper.LastTickAsync;

        calls.ShouldBe(2);
        log.Entries.ShouldBe([("keepalive-sweep", null, nameof(TimeoutException)), ("keepalive-sweep", null, nameof(TimeoutException))]);
    }

    [Fact]
    public void Starting_twice_arms_one_timer_and_disposing_stops_it()
    {
        var time = new CapturingTimeProvider();
        var sweeper = new KeepAliveSweeper(_ => Task.CompletedTask, time);

        sweeper.Start();
        sweeper.Start();
        time.Timers.Count.ShouldBe(1);
        sweeper.Dispose();

        time.Timers[0].Timer.Disposed.ShouldBeTrue();
    }
}
