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
        public Rig(TimeSpan? laptopAhead = null, Func<FakeGcp, FakeGcp>? configure = null)
        {
            PlatformClock = new MutableClock(Launch);
            Clock = laptopAhead is { } ahead ? new MutableClock(Launch + ahead) : PlatformClock;
            Env = new JobReconcilerTests.Env((configure ?? (g => g))(new FakeGcp(PlatformClock).WithWorker(FakeWorkerMode.Never)));
        }

        /// <summary>The cloud's own clock (what the platform stamps on a read); the laptop's is <see cref="Clock"/>, the same unless a test skews it.</summary>
        public MutableClock PlatformClock { get; }

        public MutableClock Clock { get; }

        public JobReconcilerTests.Env Env { get; }

        public FakeGcp Gcp => Env.Gcp;

        public JobReconciler Reconciler(IComputeGateway? compute = null, TimeSpan? mutationTimeout = null, TimeSpan? markedEndTimeout = null) => Env.Reconciler(Clock, compute: compute, mutationTimeout: mutationTimeout, markedEndTimeout: markedEndTimeout);

        public void AdvanceMinutes(int minutes)
        {
            Clock.Advance(TimeSpan.FromMinutes(minutes));
            if (!ReferenceEquals(Clock, PlatformClock))
            {
                PlatformClock.Advance(TimeSpan.FromMinutes(minutes));
            }
        }

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

    // ---- round 2 of the cold review ----

    [Fact]
    public async Task A_row_of_a_project_other_than_the_selected_one_is_ended_in_its_own_project_with_its_own_account()
    {
        // Issue #830 (round 3 finding 1 of #476): project B is selected, the Keep-alive row belongs to project A under account sub-a. The sweep must end the VM in A, as sub-a, and touch no other project.
        var rig = new Rig(configure: g => g.WithAccount("sub-a", "a@example.org", Project).WithSelectedProject("selected-later"));
        await rig.SeedAsync("job-x", AfterKeepAliveAction.Stop, tweak: r => r with { AccountSub = "sub-a" });
        rig.AdvanceMinutes(40);
        var before = rig.Gcp.Calls.Count;

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        var calls = rig.Gcp.Calls.Skip(before).ToList();
        outcomes.Single().Action.ShouldBe(LifecycleAction.VmStopped);
        calls.Where(c => c.Method is "MarkEnding" or "StopVm").Select(c => (c.Method, c.ProjectId, c.AccountSub)).ShouldBe([("MarkEnding", Project, "sub-a"), ("StopVm", Project, "sub-a")]);
        calls.Where(c => c.ProjectId != Project).ShouldBeEmpty("no call went to the selected project");
        (await rig.StatusAsync("job-x")).ShouldBe("STOPPED");
        rig.Env.Row("job-x").ErrorCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
    }

    [Fact]
    public async Task A_row_whose_account_is_signed_out_is_left_alone_and_reported_as_needing_sign_in()
    {
        var rig = new Rig(configure: g => g.WithAccount("sub-a", "a@example.org", Project).WithAccountSignedOut("sub-a"));
        await rig.SeedAsync("job-so", AfterKeepAliveAction.Stop, tweak: r => r with { AccountSub = "sub-a" });
        rig.AdvanceMinutes(40);
        var before = rig.Gcp.Calls.Count;

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.Single().Action.ShouldBe(LifecycleAction.NeedsSignIn);
        rig.Gcp.Calls.Skip(before).Where(c => c.Method is "MarkEnding" or "StopVm" or "DeleteVm").ShouldBeEmpty();
        (await rig.StatusAsync("job-so")).ShouldBe("RUNNING");
    }

    [Fact]
    public async Task A_VM_that_was_already_stopped_when_the_app_deletes_it_gets_no_still_running_note()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-sd", AfterKeepAliveAction.Delete);
        var vm = (await rig.Gcp.FindByJobIdAsync("job-sd", CancellationToken.None)).Single();
        await rig.Gcp.StopVmAsync(vm.Name, vm.Zone, CancellationToken.None);
        rig.AdvanceMinutes(40);

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-sd")).ShouldBeNull("the user chose Delete");
        outcomes.Single().Action.ShouldBe(LifecycleAction.VmDeleted);
        rig.Env.Row("job-sd").ErrorCode.ShouldBeNull("it was not running: the note says it was");
    }

    [Fact]
    public async Task The_note_carries_no_inline_English_detail()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-nd", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);

        await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        rig.Env.Row("job-nd").ErrorCode.ShouldBe(RunErrorCodes.KeepAliveEndedByApp);
        rig.Env.Row("job-nd").ErrorDetail.ShouldBeNull("Hard Rule 13: the words are the RunError_ resw key");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(36)]
    public async Task A_completed_row_with_no_finish_time_is_skipped_and_logged_never_dated_from_its_start(int minutes)
    {
        // Round 4 finding 9: a missing FinishedAt used to fall back to CreatedUtc, which would end a VM that is still inside its real window.
        var rig = new Rig();
        await rig.SeedAsync("job-nf", AfterKeepAliveAction.Stop, tweak: r => r with { FinishedAt = null });
        rig.AdvanceMinutes(minutes);
        var reconciler = rig.Reconciler();

        var outcomes = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty();
        (await rig.StatusAsync("job-nf")).ShouldBe("RUNNING");
        rig.Gcp.MarkEndingAttempts.ShouldBe(0);
        if (minutes == 36)
        {
            rig.Env.Log.Entries.Count(e => e.Source == "keepalive-sweep-skipped" && e.JobId == "job-nf" && e.ErrorClass == "NoFinishTime").ShouldBe(1, "logged once per row, not per tick");
        }
    }

    [Fact]
    public async Task A_laptop_clock_an_hour_fast_does_not_end_a_VM_whose_window_has_not_run_on_the_platform_clock()
    {
        // M4: the laptop says the window ended; the platform's own clock (the read's server time) says it did not.
        var rig = new Rig(laptopAhead: TimeSpan.FromHours(1));
        await rig.SeedAsync("job-fast", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(10);

        var outcomes = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty();
        (await rig.StatusAsync("job-fast")).ShouldBe("RUNNING", "a skewed laptop clock must not end a VM early");
        rig.Env.Row("job-fast").ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task A_small_skew_does_not_hold_back_a_VM_that_really_is_past_its_window()
    {
        var rig = new Rig(laptopAhead: TimeSpan.FromMinutes(1));
        await rig.SeedAsync("job-small", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);

        await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-small")).ShouldBe("STOPPED");
    }

    // ---- M2: a VM that run B claims between the sweep's look and its delete is B's ----

    /// <summary>The fake behind a compute interface, with a hook after the label search and one before the end call, so a test can run another run's reuse at exactly that moment.</summary>
    private sealed class RaceCompute(FakeGcp inner) : IComputeGateway
    {
        public Func<Task>? AfterFind { get; set; }

        public Func<Task>? BeforeEnd { get; set; }

        /// <summary>When set, an end call (stop or delete) never answers on its own: it waits for its token, like a delete that keeps failing and being retried.</summary>
        public bool HangEnd { get; set; }

        /// <summary>When set, an end call (stop or delete) fails with this error.</summary>
        public Exception? EndFailure { get; set; }

        public async Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
        {
            var found = await inner.FindByJobIdAsync(scope, jobId, cancellationToken);
            if (AfterFind is { } hook)
            {
                AfterFind = null;
                await hook();
            }

            return found;
        }

        /// <summary>When set, the fence answers null (the VM is gone, another job's, or the write lost a race) without writing.</summary>
        public bool MarkReturnsNull { get; set; }

        public Task<VmDescriptor?> MarkEndingAsync(CloudScope scope, string vmName, string zone, string jobId, CancellationToken cancellationToken) => MarkReturnsNull ? Task.FromResult<VmDescriptor?>(null) : inner.MarkEndingAsync(scope, vmName, zone, jobId, cancellationToken);

        public async Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        {
            await RunBeforeEnd(cancellationToken);
            await inner.StopVmAsync(scope, vmName, zone, cancellationToken);
        }

        public async Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        {
            await RunBeforeEnd(cancellationToken);
            await inner.DeleteVmAsync(scope, vmName, zone, cancellationToken);
        }

        private async Task RunBeforeEnd(CancellationToken cancellationToken = default)
        {
            if (HangEnd)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (EndFailure is { } failure)
            {
                throw failure;
            }

            if (BeforeEnd is { } hook)
            {
                BeforeEnd = null;
                await hook();
            }
        }

        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(scope, spec, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(scope, spec, existingVmName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(scope, acceleratorType, cancellationToken);

        public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(scope, installationId, cancellationToken);

        public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(scope, diskName, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(scope, installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken) => inner.ListAppVmsAsync(scope, cancellationToken);
    }

    private static async Task<VmDescriptor> StoppedKeptVmAsync(Rig rig, string jobId)
    {
        await rig.SeedAsync(jobId, AfterKeepAliveAction.Delete);
        var vm = (await rig.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).Single();
        await rig.Gcp.StopVmAsync(vm.Name, vm.Zone, CancellationToken.None); // the worker stopped it; the user chose Delete, so the app has a delete to do
        return vm;
    }

    [Fact]
    public async Task A_VM_run_B_reuses_between_the_label_search_and_the_end_is_left_running_for_B()
    {
        var rig = new Rig();
        var vm = await StoppedKeptVmAsync(rig, "job-ra");
        rig.AdvanceMinutes(40);
        var race = new RaceCompute(rig.Gcp)
        {
            AfterFind = async () => await rig.Gcp.ReuseVmAsync(SpecOf("job-rb"), vm.Name, vm.Zone, CancellationToken.None),
        };

        var outcomes = await rig.Reconciler(race).EnforceKeepAliveAsync(CancellationToken.None);

        var now = await rig.Gcp.GetVmAsync(vm.Name, vm.Zone, CancellationToken.None);
        now.ShouldNotBeNull("run B's VM was deleted by run A's window");
        now!.Status.ShouldBe("RUNNING");
        now.Labels!["job-id"].ShouldBe("job-rb");
        outcomes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reuse_attempted_after_the_sweep_marked_the_VM_for_ending_is_refused_and_changes_nothing()
    {
        var rig = new Rig();
        var vm = await StoppedKeptVmAsync(rig, "job-rc");
        rig.AdvanceMinutes(40);
        Exception? refused = null;
        var race = new RaceCompute(rig.Gcp)
        {
            BeforeEnd = async () =>
            {
                refused = await Should.ThrowAsync<CloudOperationException>(() => rig.Gcp.ReuseVmAsync(SpecOf("job-rd"), vm.Name, vm.Zone, CancellationToken.None));
            },
        };

        await rig.Reconciler(race).EnforceKeepAliveAsync(CancellationToken.None);

        refused.ShouldNotBeNull("the hook never ran: the sweep did not reach its end call (vacuous)");
        ((CloudOperationException)refused!).Error.Code.ShouldBe("VM_ENDING");
        (await rig.StatusAsync("job-rc")).ShouldBeNull("the delete went ahead");
        (await rig.StatusAsync("job-rd")).ShouldBeNull("B got no VM");
    }

    [Fact]
    public void A_recent_ending_mark_is_read_back_and_an_old_one_expires()
    {
        var now = Launch;
        var labels = new Dictionary<string, string> { [VmEnding.LabelKey] = VmEnding.Stamp(now - TimeSpan.FromMinutes(2)) };

        VmEnding.IsEnding(labels, now).ShouldBeTrue();
        VmEnding.IsEnding(labels, now + VmEnding.Window + TimeSpan.FromMinutes(5)).ShouldBeFalse("a stale mark must not make a stopped VM unreusable for ever");
        VmEnding.IsEnding(null, now).ShouldBeFalse();
        VmEnding.IsEnding(new Dictionary<string, string> { [VmEnding.LabelKey] = "garbage" }, now).ShouldBeFalse();
    }

    // ---- round 3 of #476 ----

    [Fact]
    public async Task A_laptop_clock_that_disagrees_with_the_platform_marks_nothing()
    {
        // Low finding: the mark used to be written BEFORE the skew check, so a skipped VM stayed unreusable for the mark window.
        var rig = new Rig(laptopAhead: TimeSpan.FromHours(1));
        await rig.SeedAsync("job-sk", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);

        await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        rig.Gcp.MarkEndingAttempts.ShouldBe(0, "a VM the sweep does not end must not be marked");
        (await rig.StatusAsync("job-sk")).ShouldBe("RUNNING");
        (await rig.Gcp.FindByJobIdAsync("job-sk", CancellationToken.None)).Single().Labels!.ShouldNotContainKey(VmEnding.LabelKey);
        rig.Env.Log.Entries.Count(e => e.Source == "keepalive-sweep-skipped" && e.JobId == "job-sk" && e.ErrorClass == "ClockSkew").ShouldBe(1);
    }

    [Fact]
    public async Task A_read_with_no_platform_time_is_logged_as_no_server_clock_not_as_skew_and_marks_nothing()
    {
        var rig = new Rig(configure: g => g.WithoutServerTime());
        await rig.SeedAsync("job-ns", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);

        await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        rig.Gcp.MarkEndingAttempts.ShouldBe(0);
        (await rig.StatusAsync("job-ns")).ShouldBe("RUNNING");
        rig.Env.Log.Entries.Count(e => e.Source == "keepalive-sweep-skipped" && e.JobId == "job-ns" && e.ErrorClass == "NoServerClock").ShouldBe(1);
        rig.Env.Log.Entries.Any(e => e.ErrorClass == "ClockSkew").ShouldBeFalse();
    }

    [Fact]
    public async Task A_VM_that_already_reached_its_wanted_state_between_the_search_and_the_end_is_not_marked()
    {
        var rig = new Rig();
        await rig.SeedAsync("job-st", AfterKeepAliveAction.Stop);
        var vm = (await rig.Gcp.FindByJobIdAsync("job-st", CancellationToken.None)).Single();
        rig.AdvanceMinutes(40);
        var race = new RaceCompute(rig.Gcp)
        {
            AfterFind = async () => await rig.Gcp.StopVmAsync(vm.Name, vm.Zone, CancellationToken.None), // the worker's own end lands first
        };

        var outcomes = await rig.Reconciler(race).EnforceKeepAliveAsync(CancellationToken.None);

        outcomes.ShouldBeEmpty();
        rig.Gcp.MarkEndingAttempts.ShouldBe(0, "nothing is left to end, so nothing is marked");
    }

    [Fact]
    public async Task An_end_call_that_never_answers_is_cut_off_inside_the_mark_window_and_the_next_sweep_marks_again_before_ending()
    {
        // Round 3 finding: the stop or delete was not tied to the mark. A retried delete must not land after the mark expired, when a newer run may have reused the VM.
        var rig = new Rig();
        var vm = await StoppedKeptVmAsync(rig, "job-hg");
        rig.AdvanceMinutes(40);
        var hanging = new RaceCompute(rig.Gcp) { HangEnd = true };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var first = await rig.Reconciler(hanging, mutationTimeout: TimeSpan.FromSeconds(30), markedEndTimeout: TimeSpan.FromMilliseconds(100)).EnforceKeepAliveAsync(CancellationToken.None);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the end call outlived its budget: it would outlive the mark too");
        first.Single().Action.ShouldBe(LifecycleAction.Deferred);
        (await rig.StatusAsync("job-hg")).ShouldBe("STOPPED", "the delete never landed");
        var marked = (await rig.Gcp.FindByJobIdAsync("job-hg", CancellationToken.None)).Single();
        marked.Labels!.ShouldContainKey(VmEnding.LabelKey);

        rig.AdvanceMinutes(20); // the mark has expired by now
        VmEnding.IsEnding(marked.Labels, rig.Clock.GetUtcNow()).ShouldBeFalse();
        var second = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        second.Single().Action.ShouldBe(LifecycleAction.VmDeleted);
        rig.Gcp.MarkEndingAttempts.ShouldBe(2, "a new attempt after the mark expired marks again right before its end call");
        (await rig.StatusAsync("job-hg")).ShouldBeNull();
        vm.Name.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void The_default_end_budget_is_inside_the_mark_window()
    {
        // The only non-behavioural arm: the default the app really runs with. The behaviour above is proven with a shortened budget.
        VmEnding.EndBudget.ShouldBeLessThan(VmEnding.Window);
        VmEnding.EndBudget.ShouldBeGreaterThan(TimeSpan.FromMinutes(5), "it must outlast the reconciler's own mutation deadline, or the cap would be the tighter one for no reason");
    }

    // ---- round 4 of #476 ----

    [Fact]
    public async Task A_second_sweep_inside_the_mark_window_gives_its_end_call_only_what_is_left_of_the_window()
    {
        // Round 4 finding 1: a mark still inside its window was reused with a FRESH end budget, so the end call could outlive the mark and a late-accepted delete hit a reused VM.
        var rig = new Rig();
        await StoppedKeptVmAsync(rig, "job-b1");
        rig.AdvanceMinutes(40);
        var hanging = new RaceCompute(rig.Gcp) { HangEnd = true };
        await rig.Reconciler(hanging, mutationTimeout: TimeSpan.FromSeconds(30), markedEndTimeout: TimeSpan.FromMilliseconds(100)).EnforceKeepAliveAsync(CancellationToken.None);
        var markedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse((await rig.Gcp.FindByJobIdAsync("job-b1", CancellationToken.None)).Single().Labels![VmEnding.LabelKey], System.Globalization.CultureInfo.InvariantCulture));
        rig.AdvanceMinutes(11);
        TimeSpan? chosen = null;
        var second = rig.Reconciler();
        second.EndBudgetChosen = (_, budget) => chosen = budget;

        await second.EnforceKeepAliveAsync(CancellationToken.None);

        chosen.ShouldNotBeNull("the second sweep never reached its end call (vacuous)");
        (rig.Clock.GetUtcNow() + chosen!.Value).ShouldBeLessThan(markedAt + VmEnding.Window, "the end call must end before the mark does");
        (await rig.StatusAsync("job-b1")).ShouldBeNull("with time left, the delete still goes ahead");
    }

    [Fact]
    public async Task A_mark_about_to_expire_is_not_ended_under_and_the_next_sweep_after_it_expired_marks_again()
    {
        var rig = new Rig();
        await StoppedKeptVmAsync(rig, "job-b2");
        rig.AdvanceMinutes(40);
        var hanging = new RaceCompute(rig.Gcp) { HangEnd = true };
        await rig.Reconciler(hanging, mutationTimeout: TimeSpan.FromSeconds(30), markedEndTimeout: TimeSpan.FromMilliseconds(100)).EnforceKeepAliveAsync(CancellationToken.None);
        rig.AdvanceMinutes(14); // under the safety margin of the 15 minute window
        var reconciler = rig.Reconciler();

        var late = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        late.ShouldBeEmpty("no end call may start with less of the mark left than the margin");
        (await rig.StatusAsync("job-b2")).ShouldBe("STOPPED");
        rig.Env.Log.Entries.Count(e => e.Source == "keepalive-sweep-skipped" && e.JobId == "job-b2" && e.ErrorClass == "MarkExpiring").ShouldBe(1);
        rig.AdvanceMinutes(3);

        var after = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        after.Single().Action.ShouldBe(LifecycleAction.VmDeleted);
        rig.Gcp.MarkEndingAttempts.ShouldBe(3, "the expired mark was written again right before this end call");
    }

    [Fact]
    public async Task A_fence_write_an_org_policy_refuses_is_logged_as_org_policy_never_read_as_a_lost_race_and_the_VM_is_not_ended_unfenced()
    {
        // Round 4 finding 2. DECISION (agent-made, 2026-10-31, reversible): without the fence the VM is NOT ended. The fence protects a VM another run is claiming; that claim is
        // itself a setLabels the same policy would refuse, but nothing proves it, and a delete cannot be undone. maxRunDuration stays the backstop; the log line is the trace.
        var rig = new Rig(configure: g => g.WithMarkEndingFailure(CloudErrorKind.OrgPolicy));
        await StoppedKeptVmAsync(rig, "job-op");
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        var first = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        first.Single().Action.ShouldBe(LifecycleAction.Failed);
        (await rig.StatusAsync("job-op")).ShouldBe("STOPPED", "not ended without the fence");
        rig.Env.Log.Entries.Count(e => e.Source == "keepalive-sweep-skipped" && e.JobId == "job-op" && e.ErrorClass == "MarkEndingFailed:org_policy").ShouldBe(1, "logged with its class, once per job and reason");
        rig.Gcp.MarkEndingAttempts.ShouldBe(2, "the next sweep tries again");
    }

    [Fact]
    public async Task A_fence_that_answers_null_is_logged_not_dropped_silently()
    {
        var rig = new Rig();
        await StoppedKeptVmAsync(rig, "job-nl");
        rig.AdvanceMinutes(40);
        var race = new RaceCompute(rig.Gcp) { MarkReturnsNull = true };
        var reconciler = rig.Reconciler(race);

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-nl")).ShouldBe("STOPPED");
        rig.Env.Log.Entries.Count(e => e.Source == "keepalive-sweep-skipped" && e.JobId == "job-nl" && e.ErrorClass == "MarkEndingNotApplied").ShouldBe(1);
    }

    [Fact]
    public async Task A_row_whose_VM_is_confirmed_ended_is_not_looked_up_again_by_later_sweeps()
    {
        // Round 4 finding 5: every Completed keep row stayed in the sweep for 24 hours, one FindByJobId per row every tick, even once its VM was settled.
        var rig = new Rig();
        // Round 5 finding 1: only a DELETED VM is terminal. A stopped one can be started again from the Cloud page, so it is looked at every tick (see the restart test).
        await rig.SeedAsync("job-dn", AfterKeepAliveAction.Delete);
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        (await rig.StatusAsync("job-dn")).ShouldBeNull();
        var before = rig.Gcp.Calls.Count(c => c.Method == "FindByJobId");
        before.ShouldBeGreaterThan(0, "the call name this test counts is wrong (vacuous)");
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        rig.Gcp.Calls.Count(c => c.Method == "FindByJobId").ShouldBe(before, "a settled row is skipped");
    }

    [Fact]
    public async Task A_row_whose_listing_comes_back_empty_is_looked_at_again_because_an_empty_listing_is_not_proof_the_VM_is_gone()
    {
        // Round 5 finding 1: an empty listing is also what a reuse that borrowed the VM looks like, so it is never settled.
        var rig = new Rig();
        await rig.SeedAsync("job-gn", AfterKeepAliveAction.Delete);
        var gone = (await rig.Gcp.FindByJobIdAsync("job-gn", CancellationToken.None)).Single();
        await rig.Gcp.DeleteVmAsync(gone.Name, gone.Zone, CancellationToken.None);
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        var afterFirst = rig.Gcp.Calls.Count(c => c.Method == "FindByJobId");
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        afterFirst.ShouldBeGreaterThan(0);
        rig.Gcp.Calls.Count(c => c.Method == "FindByJobId").ShouldBeGreaterThan(afterFirst);
    }

    [Fact]
    public async Task A_stopped_VM_the_user_starts_again_is_stopped_again_by_a_later_sweep_of_the_same_process()
    {
        // Round 5 finding 1: STOPPED was remembered as settled for the process, so a VM started from the Cloud page ran to maxRunDuration.
        var rig = new Rig();
        await rig.SeedAsync("job-rs", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        (await rig.StatusAsync("job-rs")).ShouldBe("STOPPED");
        var vm = (await rig.Gcp.FindByJobIdAsync("job-rs", CancellationToken.None)).Single();
        await rig.Gcp.StartVmAsync(vm.Name, vm.Zone, CancellationToken.None);
        (await rig.StatusAsync("job-rs")).ShouldBe("RUNNING");
        rig.AdvanceMinutes(10);

        var again = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-rs")).ShouldBe("STOPPED");
        again.Single().Action.ShouldBe(LifecycleAction.VmStopped);
    }

    [Fact]
    public async Task A_VM_a_reuse_borrowed_before_the_first_look_is_ended_by_a_later_sweep_of_the_same_process_once_the_labels_come_back()
    {
        var rig = new Rig();
        var donor = await StoppedKeptVmAsync(rig, "job-eb");
        rig.AdvanceMinutes(40);
        var donorLabels = donor.Labels!;
        await rig.Gcp.ReuseVmAsync(SpecOf("job-ec"), donor.Name, donor.Zone, CancellationToken.None); // the borrow: the listing under job-eb is empty
        var reconciler = rig.Reconciler();

        var first = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        first.ShouldBeEmpty();
        await rig.Gcp.StopVmAsync(donor.Name, donor.Zone, CancellationToken.None);
        (await rig.Gcp.RestoreVmLabelsAsync(donor.Name, donor.Zone, "job-ec", donorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Restored);
        rig.AdvanceMinutes(5);

        var second = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        second.Single().Action.ShouldBe(LifecycleAction.VmDeleted);
        (await rig.StatusAsync("job-eb")).ShouldBeNull("the donor chose Delete and the labels are back: the SAME process ends it");
    }

    [Fact]
    public async Task A_VM_relabelled_between_the_search_and_the_end_is_ended_by_a_later_sweep_of_the_same_process_once_the_labels_come_back()
    {
        var rig = new Rig();
        var donor = await StoppedKeptVmAsync(rig, "job-ra");
        rig.AdvanceMinutes(40);
        var donorLabels = donor.Labels!;
        var race = new RaceCompute(rig.Gcp)
        {
            AfterFind = async () => await rig.Gcp.ReuseVmAsync(SpecOf("job-rb"), donor.Name, donor.Zone, CancellationToken.None),
        };
        var reconciler = rig.Reconciler(race);

        (await reconciler.EnforceKeepAliveAsync(CancellationToken.None)).ShouldBeEmpty();
        await rig.Gcp.StopVmAsync(donor.Name, donor.Zone, CancellationToken.None);
        (await rig.Gcp.RestoreVmLabelsAsync(donor.Name, donor.Zone, "job-rb", donorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Restored);
        rig.AdvanceMinutes(5);

        var second = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        second.Single().Action.ShouldBe(LifecycleAction.VmDeleted);
    }

    [Fact]
    public async Task A_failed_end_leaves_the_row_unsettled_so_the_next_sweep_looks_again()
    {
        // Round 5 finding 4: Delete rows, because a stopped row is never settled now; here the unsettled path itself is what is proven.
        var rig = new Rig();
        await StoppedKeptVmAsync(rig, "job-fe");
        rig.AdvanceMinutes(40);
        var race = new RaceCompute(rig.Gcp) { EndFailure = new CloudOperationException(new CloudError("INTERNAL", 500, "boom"), CloudErrorKind.Other) };
        var reconciler = rig.Reconciler(race);

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        var afterFirst = rig.Gcp.Calls.Count(c => c.Method == "FindByJobId");
        race.EndFailure = null;
        rig.AdvanceMinutes(5);
        var second = await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        rig.Gcp.Calls.Count(c => c.Method == "FindByJobId").ShouldBeGreaterThan(afterFirst, "a failed end must not settle the row");
        second.Single().Action.ShouldBe(LifecycleAction.VmDeleted);
        (await rig.StatusAsync("job-fe")).ShouldBeNull();
    }

    [Fact]
    public async Task A_delete_row_skipped_for_clock_skew_is_looked_at_again_by_the_next_sweep()
    {
        var rig = new Rig(laptopAhead: TimeSpan.FromHours(1));
        await rig.SeedAsync("job-cs", AfterKeepAliveAction.Delete);
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        var afterFirst = rig.Gcp.Calls.Count(c => c.Method == "FindByJobId");
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        rig.Gcp.Calls.Count(c => c.Method == "FindByJobId").ShouldBeGreaterThan(afterFirst);
        (await rig.StatusAsync("job-cs")).ShouldBe("RUNNING");
    }

    [Fact]
    public void An_implausible_mark_is_no_mark_and_adding_the_window_to_one_cannot_overflow()
    {
        // Round 5 finding 3: a mark near DateTime max was accepted and markedAt + Window overflowed.
        var now = Launch;
        VmEnding.TryMarkedAt(new Dictionary<string, string> { [VmEnding.LabelKey] = "253402300799" }, now, out _).ShouldBeFalse();
        VmEnding.TryMarkedAt(new Dictionary<string, string> { [VmEnding.LabelKey] = VmEnding.Stamp(now + VmEnding.Window + TimeSpan.FromHours(1)) }, now, out _).ShouldBeFalse("a mark far in the future cannot be this clock's");
        VmEnding.TryMarkedAt(new Dictionary<string, string> { [VmEnding.LabelKey] = VmEnding.Stamp(now + TimeSpan.FromMinutes(2)) }, now, out var near).ShouldBeTrue("a little skew is still a mark");
        near.ShouldBe(now + TimeSpan.FromMinutes(2));
        VmEnding.TryMarkedAt(new Dictionary<string, string> { [VmEnding.LabelKey] = VmEnding.Stamp(now - TimeSpan.FromMinutes(2)) }, now, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task No_platform_time_is_logged_once_per_process_not_once_per_row()
    {
        // Round 4 finding 6. maxRunDuration stays the backstop for a proxy that strips the Date header for good.
        var rig = new Rig(configure: g => g.WithoutServerTime());
        await rig.SeedAsync("job-n1", AfterKeepAliveAction.Stop);
        await rig.SeedAsync("job-n2", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        rig.Env.Log.Entries.Count(e => e.Source == "keepalive-sweep-skipped" && e.ErrorClass == "NoServerClock").ShouldBe(1);
    }

    [Fact]
    public async Task The_sweeps_memory_is_pruned_when_a_row_leaves_the_sweep_set()
    {
        // Round 4 finding 7: the skip-log memory only ever grew.
        var rig = new Rig(laptopAhead: TimeSpan.FromHours(1));
        await rig.SeedAsync("job-pr", AfterKeepAliveAction.Stop);
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        reconciler.KeepAliveBookkeepingCount.ShouldBe(1, "the skip was remembered (vacuous otherwise)");
        rig.AdvanceMinutes(60 * 48);
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        reconciler.KeepAliveBookkeepingCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_settled_memory_is_pruned_too_when_a_settled_row_leaves_the_sweep_set()
    {
        // Round 5 finding 5: the test above only ever filled the skip-log memory.
        var rig = new Rig();
        await rig.SeedAsync("job-pd", AfterKeepAliveAction.Delete);
        rig.AdvanceMinutes(40);
        var reconciler = rig.Reconciler();

        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);
        (await rig.StatusAsync("job-pd")).ShouldBeNull();
        reconciler.KeepAliveBookkeepingCount.ShouldBe(1, "the settled row was remembered (vacuous otherwise)");
        rig.AdvanceMinutes(60 * 48);
        await reconciler.EnforceKeepAliveAsync(CancellationToken.None);

        reconciler.KeepAliveBookkeepingCount.ShouldBe(0);
    }

    // ---- #970 interplay: a failed reuse is restored, and the restore and the sweep's mark never fight ----

    [Fact]
    public async Task A_VM_a_reuse_claimed_is_skipped_unmarked_and_is_ended_by_the_next_sweep_once_the_restore_gave_it_back()
    {
        var rig = new Rig();
        var donor = await StoppedKeptVmAsync(rig, "job-da");
        rig.AdvanceMinutes(40);
        var donorLabels = donor.Labels!;
        var race = new RaceCompute(rig.Gcp)
        {
            AfterFind = async () => await rig.Gcp.ReuseVmAsync(SpecOf("job-db"), donor.Name, donor.Zone, CancellationToken.None), // B claims the VM after the label search
        };

        var first = await rig.Reconciler(race).EnforceKeepAliveAsync(CancellationToken.None);

        first.ShouldBeEmpty();
        rig.Gcp.MarkEndingAttempts.ShouldBe(0, "a VM that is B's now is never marked for A");
        await rig.Gcp.StopVmAsync(donor.Name, donor.Zone, CancellationToken.None);
        (await rig.Gcp.RestoreVmLabelsAsync(donor.Name, donor.Zone, "job-db", donorLabels, CancellationToken.None)).ShouldBe(LabelRestoreState.Restored);

        var second = await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        second.Single().Action.ShouldBe(LifecycleAction.VmDeleted);
        (await rig.StatusAsync("job-da")).ShouldBeNull("A's window still holds after the failed reuse was restored: the app ends it");
    }

    [Fact]
    public async Task A_restore_for_a_borrowing_run_never_erases_the_mark_the_sweep_put_on_the_donors_own_VM()
    {
        var rig = new Rig();
        var donor = await StoppedKeptVmAsync(rig, "job-ma");
        rig.AdvanceMinutes(40);
        var marked = await rig.Gcp.MarkEndingAsync(donor.Name, donor.Zone, "job-ma", CancellationToken.None);
        marked!.Labels!.ShouldContainKey(VmEnding.LabelKey);

        // A late restore for a run that borrowed nothing of this VM's current state (its job id is not the VM's): left exactly as it is.
        var state = await rig.Gcp.RestoreVmLabelsAsync(donor.Name, donor.Zone, "job-mb", donor.Labels!, CancellationToken.None);

        state.ShouldBe(LabelRestoreState.NewerOwner);
        (await rig.Gcp.FindByJobIdAsync("job-ma", CancellationToken.None)).Single().Labels!.ShouldContainKey(VmEnding.LabelKey);
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
    public async Task A_sweep_cancelled_by_something_other_than_shutdown_is_logged_by_class_and_the_next_tick_runs()
    {
        // Round 4 finding 8: only the sweeper's own stop is swallowed silently; any other cancel (a lookup deadline) is a failed sweep and is logged.
        var time = new CapturingTimeProvider();
        var log = new JobReconcilerTests.RecordingLog();
        var calls = 0;
        using var sweeper = new KeepAliveSweeper(_ => { calls++; throw new OperationCanceledException(); }, time, log);

        sweeper.Start();
        time.Timers[0].Callback(null);
        await sweeper.LastTickAsync;
        time.Timers[0].Callback(null);
        await sweeper.LastTickAsync;

        calls.ShouldBe(2);
        log.Entries.ShouldBe([("keepalive-sweep", null, nameof(OperationCanceledException)), ("keepalive-sweep", null, nameof(OperationCanceledException))]);
    }

    private sealed class CancellingLog : IDiagnosticsLog
    {
        public void Warning(string source, string? jobId, string errorClass) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task A_log_that_throws_a_cancel_does_not_fault_the_last_tick_and_the_next_tick_still_runs()
    {
        // Round 5 finding 6: LastTickAsync is documented never to fault; a cancel thrown by the log used to escape the handler.
        var time = new CapturingTimeProvider();
        var calls = 0;
        using var sweeper = new KeepAliveSweeper(_ => { calls++; throw new TimeoutException(); }, time, new CancellingLog());

        sweeper.Start();
        time.Timers[0].Callback(null);
        await sweeper.LastTickAsync;
        time.Timers[0].Callback(null);
        await sweeper.LastTickAsync;

        calls.ShouldBe(2);
    }

    [Fact]
    public void Disposing_twice_is_fine_and_a_start_after_it_throws()
    {
        var time = new CapturingTimeProvider();
        var sweeper = new KeepAliveSweeper(_ => Task.CompletedTask, time);
        sweeper.Start();

        sweeper.Dispose();
        sweeper.Dispose();

        time.Timers[0].Timer.Disposed.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(sweeper.Start);
        time.Timers.Count.ShouldBe(1, "no timer was armed after dispose");
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
