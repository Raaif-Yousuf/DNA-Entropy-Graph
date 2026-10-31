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
    [InlineData(29, false)]
    [InlineData(36, true)]
    public async Task A_row_with_no_finish_time_counts_from_its_start_like_the_launch_pass(int minutes, bool ended)
    {
        // The seeded row was created five minutes before launch: window end = created + 30, then the 5 minute grace.
        var rig = new Rig();
        await rig.SeedAsync("job-nf", AfterKeepAliveAction.Stop, tweak: r => r with { FinishedAt = null });
        rig.AdvanceMinutes(minutes);

        await rig.Reconciler().EnforceKeepAliveAsync(CancellationToken.None);

        (await rig.StatusAsync("job-nf")).ShouldBe(ended ? "STOPPED" : "RUNNING");
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

        public Task<VmDescriptor?> MarkEndingAsync(CloudScope scope, string vmName, string zone, string jobId, CancellationToken cancellationToken) => inner.MarkEndingAsync(scope, vmName, zone, jobId, cancellationToken);

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
