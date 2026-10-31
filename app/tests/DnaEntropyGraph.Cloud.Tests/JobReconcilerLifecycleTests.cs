using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

internal sealed class MemorySettingsStore : ISettingsStore
{
    public Dictionary<string, string> Values { get; } = new();

    public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public void SetString(string key, string value) => Values[key] = value;
}

/// <summary>
/// Issue #530 (part b of #59): at launch and on reconnect the reconciler also enforces the lifecycle a finished run's VM was labelled
/// with (Hard Rule 11), deletes this installation's VMs that sat stopped too long (Hard Rule 9: by label, never another installation's),
/// and is run again when the connection comes back, without ever driving a run something else already owns.
/// </summary>
public class JobReconcilerLifecycleTests
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
        public Rig(Func<FakeGcp, FakeGcp>? script = null)
        {
            Clock = new MutableClock(Launch);
            Env = new JobReconcilerTests.Env(script is null ? new FakeGcp(Clock).WithWorker(FakeWorkerMode.Never) : script(new FakeGcp(Clock).WithWorker(FakeWorkerMode.Never)));
        }

        public MutableClock Clock { get; }

        public JobReconcilerTests.Env Env { get; }

        public FakeGcp Gcp => Env.Gcp;

        public JobReconciler Reconciler(TimeSpan? lookupTimeout = null, TimeSpan? mutationTimeout = null, IComputeGateway? compute = null, DnaEntropyGraph.Core.Abstractions.IDiagnosticsLog? log = null, Func<IEnumerable<CloudScope>>? additionalSweepScopes = null, Action<string?>? onSignInNeeded = null) => Env.Reconciler(Clock, lookupTimeout, mutationTimeout, log: log, compute: compute, additionalSweepScopes: additionalSweepScopes, onSignInNeeded: onSignInNeeded);

        /// <summary>A finished run (<paramref name="phase"/>) whose VM exists, finished ten minutes before launch.</summary>
        public async Task SeedFinishedAsync(
            string jobId,
            AfterTaskAction after,
            JobPhase phase = JobPhase.Completed,
            AfterKeepAliveAction afterKeepAlive = AfterKeepAliveAction.Stop,
            int keepAliveMinutes = 30,
            Func<RunRecord, RunRecord>? tweak = null)
        {
            await Env.SeedAsync(
                jobId,
                phase,
                vm: true,
                after: after,
                afterKeepAlive: afterKeepAlive,
                keepAliveMinutes: keepAliveMinutes,
                tweak: row => (tweak ?? (r => r))(row with { FinishedAt = Launch.AddMinutes(-10) }));
        }

        public async Task<IReadOnlyList<VmDescriptor>> VmsAsync(string jobId) => await Gcp.FindByJobIdAsync(jobId, CancellationToken.None);

        public async Task StopAsync(string jobId)
        {
            var vm = (await VmsAsync(jobId)).Single();
            await Gcp.StopVmAsync(vm.Name, vm.Zone, CancellationToken.None);
        }
    }

    // ---- 1. a finished run's VM in a state its lifecycle label forbids ----

    [Theory]
    [InlineData("STOPPED")]
    [InlineData("RUNNING")]
    public async Task A_finished_run_with_lifecycle_delete_whose_VM_still_exists_has_it_deleted(string state)
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-del", AfterTaskAction.Delete);
        if (state == "STOPPED")
        {
            await rig.StopAsync("job-del");
        }

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-del")).ShouldBeEmpty("the VM must be found by its job-id label and deleted");
        outcomes.ShouldContain(o => o.JobId == "job-del" && o.Action == LifecycleAction.VmDeleted);
    }

    [Fact]
    public async Task A_finished_run_with_lifecycle_stop_keeps_its_stopped_VM_and_stops_a_running_one()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-stopped", AfterTaskAction.Stop);
        await rig.StopAsync("job-stopped");
        await rig.SeedFinishedAsync("job-running", AfterTaskAction.Stop);

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-stopped")).Single().Status.ShouldBe("STOPPED");
        (await rig.VmsAsync("job-running")).Single().Status.ShouldBe("STOPPED", "a stop-lifecycle VM left RUNNING after the run ended is a billing leak");
        outcomes.Select(o => (o.JobId, o.Action)).ShouldBe([("job-running", LifecycleAction.VmStopped)]);
    }

    [Fact]
    public async Task A_keep_alive_VM_before_its_expiry_is_left_running()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-keep", AfterTaskAction.KeepAlive, keepAliveMinutes: 30, afterKeepAlive: AfterKeepAliveAction.Delete);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-keep")).Single().Status.ShouldBe("RUNNING");
        outcomes.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_project_B_selected_a_keep_alive_row_in_project_A_expires_and_its_VM_is_deleted_in_A_with_As_account()
    {
        // Issue #830, the wired-to-nothing observable: the pass enforces the row's own project and account, whatever is selected at launch.
        var rig = new Rig(g => g.WithAccount("sub-a", "a@example.org", "project-a").WithSelectedProject("project-b"));
        var request = await rig.Env.SeedAsync("job-ka", JobPhase.Completed, vm: false, after: AfterTaskAction.KeepAlive, afterKeepAlive: AfterKeepAliveAction.Delete, keepAliveMinutes: 30, tweak: row => row with { FinishedAt = Launch.AddMinutes(-10), ProjectId = "project-a", AccountSub = "sub-a" });
        await rig.Gcp.CreateVmAsync(new CloudScope("project-a", "sub-a"), request.Spec with { ProjectId = "project-a" }, Zone, CancellationToken.None); // the computer really is in project-a
        rig.Clock.Advance(TimeSpan.FromMinutes(21));

        var outcomes = await rig.Reconciler(additionalSweepScopes: () => [rig.Gcp.SelectedScope()!]).EnforceLifecycleAsync(CancellationToken.None);

        outcomes.Single(o => o.JobId == "job-ka").Action.ShouldBe(LifecycleAction.VmDeleted);
        (await rig.VmsAsync("job-ka")).ShouldBeEmpty();
        var deletes = rig.Gcp.CallsTo("DeleteVm");
        deletes.ShouldNotBeEmpty();
        deletes.ShouldAllBe(c => c.ProjectId == "project-a" && c.AccountSub == "sub-a", "the delete must go to the row's project as the row's account, never to project-b");
        rig.Gcp.CallsTo("FindByJobId").Where(c => c.ProjectId == "project-b").ShouldBeEmpty("project-b was selected, but the run is not in it");
    }

    [Theory]
    [InlineData(AfterKeepAliveAction.Delete, null)]
    [InlineData(AfterKeepAliveAction.Stop, "STOPPED")]
    public async Task A_keep_alive_VM_past_its_expiry_ends_per_afterKeepAlive(AfterKeepAliveAction after, string? expected)
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-exp", AfterTaskAction.KeepAlive, keepAliveMinutes: 30, afterKeepAlive: after);
        rig.Clock.Advance(TimeSpan.FromMinutes(21));

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        var vms = await rig.VmsAsync("job-exp");
        if (expected is null)
        {
            vms.ShouldBeEmpty();
        }
        else
        {
            vms.Single().Status.ShouldBe(expected);
        }

        outcomes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_keep_alive_VM_of_a_run_that_did_not_complete_ends_at_once_per_afterKeepAlive()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-kf", AfterTaskAction.KeepAlive, phase: JobPhase.Failed, keepAliveMinutes: 240, afterKeepAlive: AfterKeepAliveAction.Delete);

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-kf")).ShouldBeEmpty("startup.sh applies afterKeepAlive at once for a run that did not succeed");
    }

    // phase, keep-alive minutes, minutes since the run finished, VM must survive. Mirrors worker/vm/startup.sh keep_hold:
    // only a worker exit 0 (Completed, PartiallyCompleted) holds the VM for the window; exit 2 (Failed) and 3 (Cancelled) end it at once.
    private static readonly (JobPhase Phase, int KeepMinutes, int FinishedAgo, bool Survives)[] KeepAliveRows =
    [
        (JobPhase.Completed, 240, 1, true),
        (JobPhase.PartiallyCompleted, 240, 1, true),
        (JobPhase.Failed, 240, 1, false),
        (JobPhase.Cancelled, 240, 1, false),
        (JobPhase.Completed, 240, 250, false),
        (JobPhase.PartiallyCompleted, 240, 250, false),
    ];

    public static TheoryData<JobPhase, int, int, bool> KeepAliveCases
    {
        get
        {
            var data = new TheoryData<JobPhase, int, int, bool>();
            foreach (var r in KeepAliveRows)
            {
                data.Add(r.Phase, r.KeepMinutes, r.FinishedAgo, r.Survives);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(KeepAliveCases))]
    public async Task A_keep_alive_VM_is_held_for_its_window_only_when_the_worker_exited_0(JobPhase phase, int keepMinutes, int finishedMinutesAgo, bool survives)
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-ka", AfterTaskAction.KeepAlive, phase: phase, keepAliveMinutes: keepMinutes, afterKeepAlive: AfterKeepAliveAction.Delete, tweak: row => row with { FinishedAt = Launch.AddMinutes(-finishedMinutesAgo) });

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-ka")).Count.ShouldBe(survives ? 1 : 0, $"{phase}, window {keepMinutes} min, finished {finishedMinutesAgo} min ago");
    }

    [Fact]
    public void The_keep_alive_case_table_covers_every_terminal_phase_and_both_outcomes()
    {
        KeepAliveRows.Select(c => c.Phase).Distinct().OrderBy(p => p).ShouldBe([JobPhase.Completed, JobPhase.PartiallyCompleted, JobPhase.Cancelled, JobPhase.Failed]);
        KeepAliveRows.Select(c => c.Survives).Distinct().Count().ShouldBe(2);
    }

    // Issue #938 (the durable backstop): a create Compute accepted after the app stopped looking, or after the app was closed mid-cancel, leaves a VM under a cancelled run's job id.
    // The in-process watch dies with the app; the next launch deletes it here, whatever lifecycle label the VM carries.
    [Theory]
    [InlineData(AfterTaskAction.Stop, JobPhase.Cancelled, null)]
    [InlineData(AfterTaskAction.Delete, JobPhase.Cancelled, null)]
    [InlineData(AfterTaskAction.KeepAlive, JobPhase.Cancelled, null)]
    [InlineData(AfterTaskAction.Stop, JobPhase.Failed, RunErrorCodes.CancelFailed)]
    public async Task A_VM_that_appears_under_a_cancelled_runs_job_id_is_deleted_on_the_next_launch(AfterTaskAction after, JobPhase phase, string? code)
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-late", after, phase: phase, tweak: row => row with { ErrorCode = code });

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-late")).ShouldBeEmpty("a VM labelled with a cancelled run's job id bills until maxRunDuration unless the launch deletes it");
        outcomes.ShouldContain(o => o.JobId == "job-late" && o.Action == LifecycleAction.VmDeleted);
    }

    [Theory]
    [InlineData(AfterTaskAction.Stop, JobPhase.Cancelled, null)]
    [InlineData(AfterTaskAction.Stop, JobPhase.Failed, RunErrorCodes.CancelFailed)]
    public async Task A_keep_alive_VM_a_cancelled_runs_reuse_relabelled_to_it_is_stopped_never_deleted(AfterTaskAction after, JobPhase phase, string? code)
    {
        // Round 3 H1: a reuse in flight at the cancel lands later and leaves ANOTHER run's VM ("deg-job-donor") labelled with the cancelled run's job id.
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-donor", AfterTaskAction.Stop);
        await rig.StopAsync("job-donor");
        var request = await rig.Env.SeedAsync("job-borrower", phase, vm: false, after: after, tweak: row => row with { ErrorCode = code, FinishedAt = Launch.AddMinutes(-10) });
        await rig.Gcp.ReuseVmAsync(request.Spec, "deg-job-donor", Zone, CancellationToken.None);

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        var vm = await rig.Gcp.GetVmAsync("deg-job-donor", Zone, CancellationToken.None);
        vm.ShouldNotBeNull("the launch deleted a keep-alive VM that another run created and the cancelled run only borrowed");
        vm.Status.ShouldBe("STOPPED", "a borrowed VM the cancel never stopped is stopped, not left billing");
        outcomes.ShouldNotContain(o => o.Action == LifecycleAction.VmDeleted && o.JobId == "job-borrower");
    }

    [Theory]
    [InlineData("RUNNING")]
    [InlineData("STOPPED")]
    public async Task A_failed_runs_donor_VM_a_failed_reuse_relabelled_lifecycle_delete_is_stopped_at_most_never_deleted(string state)
    {
        // Round 8 M3: a Failed row (not a cancel) whose job label says delete must never delete a VM that is not its own deg-<job>.
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-donor8", AfterTaskAction.Stop);
        await rig.StopAsync("job-donor8");
        var request = await rig.Env.SeedAsync("job-failed8", JobPhase.Failed, vm: false, after: AfterTaskAction.Delete, tweak: row => row with { FinishedAt = Launch.AddMinutes(-10) });
        await rig.Gcp.ReuseVmAsync(request.Spec, "deg-job-donor8", Zone, CancellationToken.None);
        if (state == "STOPPED")
        {
            await rig.Gcp.StopVmAsync("deg-job-donor8", Zone, CancellationToken.None);
        }

        (await rig.Gcp.GetVmAsync("deg-job-donor8", Zone, CancellationToken.None))!.Labels!["lifecycle"].ShouldBe("delete");

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        rig.Gcp.DeleteAttempts.ShouldBeEmpty("another run's VM and disk are never deleted by a launch");
        (await rig.Gcp.GetVmAsync("deg-job-donor8", Zone, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
    }
    [Theory]
    [InlineData("RUNNING")]
    [InlineData("STOPPED")]
    public async Task A_completed_run_on_a_reused_VM_with_lifecycle_delete_has_that_VM_deleted_on_the_next_launch(string state)
    {
        // Round 9 M2: the user's own after-run Delete of a run that COMPLETED on a borrowed VM is the one exception to "never delete a VM the run did not create".
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-donor9", AfterTaskAction.Stop);
        await rig.StopAsync("job-donor9");
        var request = await rig.Env.SeedAsync("job-done9", JobPhase.Completed, vm: false, after: AfterTaskAction.Delete, tweak: row => row with { FinishedAt = Launch.AddMinutes(-10), VmName = "deg-job-donor9", Zone = Zone, VmReused = true });
        await rig.Gcp.ReuseVmAsync(request.Spec, "deg-job-donor9", Zone, CancellationToken.None);
        if (state == "STOPPED")
        {
            await rig.Gcp.StopVmAsync("deg-job-donor9", Zone, CancellationToken.None);
        }

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.Gcp.GetVmAsync("deg-job-donor9", Zone, CancellationToken.None)).ShouldBeNull("the app died before the live delete; the launch finishes the user's choice");
        outcomes.ShouldContain(o => o.JobId == "job-done9" && o.Action == LifecycleAction.VmDeleted);
    }

    [Fact]
    public async Task A_completed_run_whose_row_names_another_VM_does_not_get_a_relabelled_donor_deleted()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-donor9b", AfterTaskAction.Stop);
        await rig.StopAsync("job-donor9b");
        var request = await rig.Env.SeedAsync("job-done9b", JobPhase.Completed, vm: false, after: AfterTaskAction.Delete, tweak: row => row with { FinishedAt = Launch.AddMinutes(-10), VmName = "deg-job-done9b", Zone = Zone });
        await rig.Gcp.ReuseVmAsync(request.Spec, "deg-job-donor9b", Zone, CancellationToken.None);

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        rig.Gcp.DeleteAttempts.ShouldBeEmpty("the row's own VM is a different one");
        (await rig.Gcp.GetVmAsync("deg-job-donor9b", Zone, CancellationToken.None)).ShouldNotBeNull();
    }
    /// <summary>A gateway whose label listing answers, and then (once) relabels the VM to another job before anyone can delete it: the race of round 10 M1.</summary>
    private sealed class RelabelAfterListingCompute(FakeGcp inner, string vmName, string zone, IReadOnlyDictionary<string, string> labels, bool onInstallationList = false) : IComputeGateway
    {
        private bool _done;

        public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.GetVmAsync(scope, n, z, ct);

        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string z, CancellationToken ct) => inner.ReuseVmAsync(scope, spec, existingVmName, z, ct);

        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string z, CancellationToken ct) => inner.CreateVmAsync(scope, spec, z, ct);

        public Task StopVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.StopVmAsync(scope, n, z, ct);

        public Task DeleteVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.DeleteVmAsync(scope, n, z, ct);

        public async Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken ct)
        {
            var found = await inner.FindByJobIdAsync(scope, jobId, ct);
            if (!onInstallationList && !_done && found.Any(v => v.Name == vmName))
            {
                _done = true;
                inner.SimulateExternalRelabel(vmName, zone, labels); // another run's reuse claims the VM after the listing
            }

            return found;
        }

        public async Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string i, CancellationToken ct)
        {
            var listed = await inner.ListByInstallationAsync(scope, i, ct);
            if (onInstallationList && !_done && listed.Any(v => v.Name == vmName))
            {
                _done = true;
                inner.SimulateExternalRelabel(vmName, zone, labels); // a reuse claims the donor after the idle sweep listed it
            }

            return listed;
        }

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken ct) => inner.ListAppVmsAsync(scope, ct);

        public Task StartVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.StartVmAsync(scope, n, z, ct);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string i, CancellationToken ct) => inner.ListDisksByInstallationAsync(scope, i, ct);

        public Task DeleteDiskAsync(CloudScope scope, string d, string z, CancellationToken ct) => inner.DeleteDiskAsync(scope, d, z, ct);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string a, CancellationToken ct) => inner.ListAcceleratorZonesAsync(scope, a, ct);
    }

    [Fact]
    public async Task A_VM_relabelled_to_another_job_between_the_listing_and_the_re_read_survives_and_the_skip_is_logged()
    {
        // Round 10 M1: the reconciler lists by label, decides Delete, and re-reads the VM right before the delete. This covers the listing-to-re-read window ONLY (round 11 M2: the re-read-to-delete window is the next test).
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-race10", AfterTaskAction.Delete);
        await rig.StopAsync("job-race10");
        var vm = (await rig.VmsAsync("job-race10")).Single();
        var otherLabels = new Dictionary<string, string>(vm.Labels!) { ["job-id"] = "job-someone-else", ["lifecycle"] = "stop" };
        var compute = new RelabelAfterListingCompute(rig.Gcp, vm.Name, vm.Zone, otherLabels);

        var log = NSubstitute.Substitute.For<DnaEntropyGraph.Core.Abstractions.IDiagnosticsLog>();

        await rig.Reconciler(log: log, compute: compute).EnforceLifecycleAsync(CancellationToken.None);

        log.Received().Warning("reconciler", "job-race10", "delete-skipped-vm-relabelled");
        rig.Gcp.DeleteAttempts.ShouldBeEmpty("the VM now belongs to another run");
        (await rig.Gcp.GetVmAsync(vm.Name, vm.Zone, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_partially_completed_run_on_a_reused_VM_with_lifecycle_delete_has_that_VM_deleted_on_the_next_launch()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-donor10p", AfterTaskAction.Stop);
        await rig.StopAsync("job-donor10p");
        var request = await rig.Env.SeedAsync("job-part10", JobPhase.PartiallyCompleted, vm: false, after: AfterTaskAction.Delete, tweak: row => row with { FinishedAt = Launch.AddMinutes(-10), VmName = "deg-job-donor10p", Zone = Zone, VmReused = true });
        await rig.Gcp.ReuseVmAsync(request.Spec, "deg-job-donor10p", Zone, CancellationToken.None);

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.Gcp.GetVmAsync("deg-job-donor10p", Zone, CancellationToken.None)).ShouldBeNull("the user chose Delete and the run finished on that VM");
    }

    [Theory]
    [InlineData(21, true)]
    [InlineData(5, false)]
    public async Task A_completed_run_on_a_reused_VM_with_keep_alive_then_Delete_is_deleted_only_after_the_expiry(int advanceMinutes, bool deleted)
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-donor10k", AfterTaskAction.Stop);
        await rig.StopAsync("job-donor10k");
        var request = await rig.Env.SeedAsync("job-keep10", JobPhase.Completed, vm: false, after: AfterTaskAction.KeepAlive, afterKeepAlive: AfterKeepAliveAction.Delete, keepAliveMinutes: 30, tweak: row => row with { FinishedAt = Launch.AddMinutes(-10), VmName = "deg-job-donor10k", Zone = Zone, VmReused = true });
        await rig.Gcp.ReuseVmAsync(request.Spec, "deg-job-donor10k", Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromMinutes(advanceMinutes));

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        var vm = await rig.Gcp.GetVmAsync("deg-job-donor10k", Zone, CancellationToken.None);
        if (deleted)
        {
            vm.ShouldBeNull();
        }
        else
        {
            vm.ShouldNotBeNull();
            vm.Status.ShouldBe("RUNNING", "keep-alive has not expired: left running");
        }
    }
    /// <summary>A gateway that relabels the VM inside the delete call, after the reconciler's re-read: the residual window (Compute's delete has no label precondition).</summary>
    private sealed class RelabelInsideDeleteCompute(FakeGcp inner, IReadOnlyDictionary<string, string> labels) : IComputeGateway
    {
        public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.GetVmAsync(scope, n, z, ct);

        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string z, CancellationToken ct) => inner.ReuseVmAsync(scope, spec, existingVmName, z, ct);

        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string z, CancellationToken ct) => inner.CreateVmAsync(scope, spec, z, ct);

        public Task StopVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.StopVmAsync(scope, n, z, ct);

        public Task DeleteVmAsync(CloudScope scope, string n, string z, CancellationToken ct)
        {
            inner.SimulateExternalRelabel(n, z, labels); // another run's setLabels lands after the re-read and before the delete
            return inner.DeleteVmAsync(scope, n, z, ct);
        }

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken ct) => inner.FindByJobIdAsync(scope, jobId, ct);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string i, CancellationToken ct) => inner.ListByInstallationAsync(scope, i, ct);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken ct) => inner.ListAppVmsAsync(scope, ct);

        public Task StartVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.StartVmAsync(scope, n, z, ct);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string i, CancellationToken ct) => inner.ListDisksByInstallationAsync(scope, i, ct);

        public Task DeleteDiskAsync(CloudScope scope, string d, string z, CancellationToken ct) => inner.DeleteDiskAsync(scope, d, z, ct);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string a, CancellationToken ct) => inner.ListAcceleratorZonesAsync(scope, a, ct);
    }

    [Fact]
    public async Task A_relabel_that_lands_between_the_re_read_and_the_delete_is_a_documented_residual_and_the_delete_lands()
    {
        // Round 11 M2, round 12 L2: this pins that the delete LANDS (the residual window), NOT that it is harmless: the re-read narrows the window and cannot close it. What then happens to that other run (its reuse fails and falls to the create ladder) is a THEORY (unverified) and is not asserted here.
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-race11", AfterTaskAction.Delete);
        await rig.StopAsync("job-race11");
        var vm = (await rig.VmsAsync("job-race11")).Single();
        var otherLabels = new Dictionary<string, string>(vm.Labels!) { ["job-id"] = "job-someone-else", ["lifecycle"] = "stop" };

        await rig.Reconciler(compute: new RelabelInsideDeleteCompute(rig.Gcp, otherLabels)).EnforceLifecycleAsync(CancellationToken.None);

        rig.Gcp.DeleteAttempts.ShouldContain(d => d.Name == vm.Name, "Compute's delete has no label precondition");
        (await rig.Gcp.GetVmAsync(vm.Name, vm.Zone, CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>A gateway whose GetVm answer is rewritten by <paramref name="map"/> (the re-read of round 12): null VM, or the VM with its labels removed.</summary>
    private sealed class GetVmMappedCompute(FakeGcp inner, Func<VmDescriptor?, VmDescriptor?> map, Func<VmDescriptor, VmDescriptor>? mapListed = null) : IComputeGateway
    {
        public async Task<VmDescriptor?> GetVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => map(await inner.GetVmAsync(scope, n, z, ct));

        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string z, CancellationToken ct) => inner.ReuseVmAsync(scope, spec, existingVmName, z, ct);

        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string z, CancellationToken ct) => inner.CreateVmAsync(scope, spec, z, ct);

        public Task StopVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.StopVmAsync(scope, n, z, ct);

        public Task DeleteVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.DeleteVmAsync(scope, n, z, ct);

        public async Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken ct)
        {
            var found = await inner.FindByJobIdAsync(scope, jobId, ct);
            return mapListed is null ? found : found.Select(mapListed).ToList();
        }

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string i, CancellationToken ct) => inner.ListByInstallationAsync(scope, i, ct);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken ct) => inner.ListAppVmsAsync(scope, ct);

        public Task StartVmAsync(CloudScope scope, string n, string z, CancellationToken ct) => inner.StartVmAsync(scope, n, z, ct);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string i, CancellationToken ct) => inner.ListDisksByInstallationAsync(scope, i, ct);

        public Task DeleteDiskAsync(CloudScope scope, string d, string z, CancellationToken ct) => inner.DeleteDiskAsync(scope, d, z, ct);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string a, CancellationToken ct) => inner.ListAcceleratorZonesAsync(scope, a, ct);
    }

    [Fact]
    public async Task A_running_VM_relabelled_to_another_job_between_the_listing_and_the_re_read_is_not_stopped_and_the_skip_is_logged()
    {
        // Round 12 M1: the Stop path re-reads too.
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-stop12", AfterTaskAction.Stop);
        var vm = (await rig.VmsAsync("job-stop12")).Single();
        vm.Status.ShouldBe("RUNNING");
        var otherLabels = new Dictionary<string, string>(vm.Labels!) { ["job-id"] = "job-someone-else", ["lifecycle"] = "keep" };
        var log = NSubstitute.Substitute.For<DnaEntropyGraph.Core.Abstractions.IDiagnosticsLog>();

        await rig.Reconciler(log: log, compute: new RelabelAfterListingCompute(rig.Gcp, vm.Name, vm.Zone, otherLabels)).EnforceLifecycleAsync(CancellationToken.None);

        log.Received().Warning("reconciler", "job-stop12", "stop-skipped-vm-relabelled");
        (await rig.Gcp.GetVmAsync(vm.Name, vm.Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING", "the VM now belongs to another run and must not be stopped under it");
    }

    [Theory]
    [InlineData("delete", "gone", "delete-skipped-vm-gone")]
    [InlineData("delete", "unlabelled", "delete-skipped-vm-unlabelled")]
    [InlineData("stop", "gone", "stop-skipped-vm-gone")]
    [InlineData("stop", "unlabelled", "stop-skipped-vm-unlabelled")]
    public async Task A_re_read_that_finds_a_listed_VM_gone_or_stripped_of_its_labels_skips_the_action_and_logs_it(string action, string how, string logged)
    {
        var rig = new Rig();
        var job = "job-skip13" + action + how;
        await rig.SeedFinishedAsync(job, action == "delete" ? AfterTaskAction.Delete : AfterTaskAction.Stop);
        if (action == "delete")
        {
            await rig.StopAsync(job);
        }

        var log = NSubstitute.Substitute.For<DnaEntropyGraph.Core.Abstractions.IDiagnosticsLog>();
        var compute = new GetVmMappedCompute(rig.Gcp, vm => how == "gone" ? null : vm! with { Labels = null });

        await rig.Reconciler(log: log, compute: compute).EnforceLifecycleAsync(CancellationToken.None);

        log.Received().Warning("reconciler", job, logged);
        rig.Gcp.DeleteAttempts.ShouldBeEmpty();
        if (action == "stop")
        {
            (await rig.Gcp.GetVmAsync("deg-" + job, Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
        }
    }

    [Fact]
    public async Task A_VM_the_listing_itself_returned_without_labels_is_judged_by_the_rows_options_and_proceeds()
    {
        // Round 13 M2: labels NOT READ (null in the listing too) are not labels removed: the documented fallback (the row's own options) applies and the delete goes ahead.
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-unread13", AfterTaskAction.Delete);
        await rig.StopAsync("job-unread13");
        var compute = new GetVmMappedCompute(rig.Gcp, vm => vm! with { Labels = null }, listed => listed with { Labels = null });

        await rig.Reconciler(compute: compute).EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-unread13")).ShouldBeEmpty("no labels were read, so there is nothing to compare and the options say Delete");
    }

    [Fact]
    public async Task An_idle_stopped_VM_a_reuse_relabels_between_the_idle_listing_and_the_re_read_is_not_deleted_and_the_skip_is_logged()
    {
        // Round 13 M1: setLabels does not change the stop time, so the idle check alone cannot see the claim.
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-idle13", AfterTaskAction.Stop);
        await rig.StopAsync("job-idle13");
        rig.Clock.Advance(TimeSpan.FromHours(80));
        var vm = (await rig.VmsAsync("job-idle13")).Single();
        var otherLabels = new Dictionary<string, string>(vm.Labels!) { ["job-id"] = "job-someone-else" };
        var log = NSubstitute.Substitute.For<DnaEntropyGraph.Core.Abstractions.IDiagnosticsLog>();

        await rig.Reconciler(log: log, compute: new RelabelAfterListingCompute(rig.Gcp, vm.Name, vm.Zone, otherLabels, onInstallationList: true)).EnforceLifecycleAsync(CancellationToken.None);

        log.Received().Warning("reconciler", "job-idle13", "idle-delete-skipped-vm-relabelled");
        rig.Gcp.DeleteAttempts.ShouldBeEmpty();
        (await rig.Gcp.GetVmAsync(vm.Name, vm.Zone, CancellationToken.None)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(RunErrorCodes.StoppedByCleanup)]
    [InlineData(RunErrorCodes.DeletedByCleanup)]
    public async Task A_run_the_user_ended_with_Stop_or_Delete_from_the_exit_window_keeps_its_stopped_VM_as_the_user_chose(string code)
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-cleanup", AfterTaskAction.Stop, phase: JobPhase.Cancelled, tweak: row => row with { ErrorCode = code });
        await rig.StopAsync("job-cleanup");

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-cleanup")).Single().Status.ShouldBe("STOPPED", "a Cancelled row the user made by choosing Stop is not a cancel's leftover");
    }

    [Fact]
    public async Task A_slow_delete_does_not_expire_under_the_lookup_deadline_and_the_next_VM_is_still_deleted()
    {
        var rig = new Rig(g => g.WithFirstDeleteDelay(TimeSpan.FromMilliseconds(600)));
        await rig.SeedFinishedAsync("job-slow-a", AfterTaskAction.Delete);
        await rig.SeedFinishedAsync("job-slow-b", AfterTaskAction.Delete);

        var outcomes = await rig.Reconciler(lookupTimeout: TimeSpan.FromMilliseconds(150)).EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-slow-a")).ShouldBeEmpty("a delete is an operation that may outlast a lookup's deadline");
        (await rig.VmsAsync("job-slow-b")).ShouldBeEmpty();
        outcomes.Count(o => o.Action == LifecycleAction.VmDeleted).ShouldBe(2);
    }

    [Fact]
    public async Task A_delete_that_outlasts_even_the_mutation_deadline_is_deferred_and_the_pass_goes_on_with_the_next_VM()
    {
        var rig = new Rig(g => g.WithFirstDeleteDelay(TimeSpan.FromSeconds(30)));
        await rig.SeedFinishedAsync("job-hang-a", AfterTaskAction.Delete);
        await rig.SeedFinishedAsync("job-hang-b", AfterTaskAction.Delete);

        var outcomes = await rig.Reconciler(lookupTimeout: TimeSpan.FromMilliseconds(100), mutationTimeout: TimeSpan.FromMilliseconds(200)).EnforceLifecycleAsync(CancellationToken.None);

        outcomes.Count(o => o.Action == LifecycleAction.Deferred).ShouldBe(1);
        outcomes.Count(o => o.Action == LifecycleAction.VmDeleted).ShouldBe(1);
        var left = (await rig.VmsAsync("job-hang-a")).Count + (await rig.VmsAsync("job-hang-b")).Count;
        left.ShouldBe(1, "one delete was deferred; the other VM was not skipped because of it");
    }

    [Fact]
    public async Task A_reconciler_mutation_that_hits_the_apps_own_deadline_is_audited_as_timeout_not_cancelled()
    {
        // Issue #245 review round 4 (M): the reconciler's own deadline token reached the audited gateway without the caller scope, so the app's timeout read "cancelled".
        var rig = new Rig(g => g.WithFirstDeleteDelay(TimeSpan.FromSeconds(30)));
        await rig.SeedFinishedAsync("job-audit-timeout", AfterTaskAction.Delete);
        var entries = new List<DnaEntropyGraph.Core.Cloud.Audit.CloudAuditEntry>();
        var auditor = new DnaEntropyGraph.Core.Cloud.Audit.CloudAuditor(new ListLog(entries), () => "install-1", TimeProvider.System);
        var audited = new AuditedComputeGateway(rig.Gcp, auditor);

        // The lookup window is wide so a slow machine cannot turn the cheap lookups into Deferred; the delete never completes, so the short mutation window still fires.
        var outcomes = await rig.Reconciler(lookupTimeout: TimeSpan.FromSeconds(10), mutationTimeout: TimeSpan.FromMilliseconds(200), compute: audited).EnforceLifecycleAsync(CancellationToken.None);

        outcomes.ShouldContain(o => o.JobId == "job-audit-timeout" && o.Action == LifecycleAction.Deferred);
        for (var i = 0; i < 500 && !entries.Any(e => e.Operation == "Compute.DeleteVm" && e.Phase == "finished"); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        entries.Single(e => e.Operation == "Compute.DeleteVm" && e.Phase == "finished").Outcome.ShouldBe("timeout");
    }

    /// <summary>The delete registers a callback that throws on the token it was handed, then waits for that token (a callee with a throwing cancellation callback).</summary>
    private sealed class ThrowingCallbackCompute(FakeGcp inner, TaskCompletionSource reached) : IComputeGateway
    {
        public Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => throw new InvalidOperationException("callee callback"));
            reached.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(scope, vmName, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(scope, spec, existingVmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(scope, spec, zone, cancellationToken);

        public Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(scope, jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(scope, installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken) => inner.ListAppVmsAsync(scope, cancellationToken);

        public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(scope, vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(scope, installationId, cancellationToken);

        public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(scope, diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(scope, acceleratorType, cancellationToken);
    }

    [Fact]
    public async Task A_user_cancel_during_a_delete_is_not_recorded_as_a_failure_when_the_deleting_call_has_a_throwing_cancellation_callback()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-cancel-throw", AfterTaskAction.Delete);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciler = rig.Reconciler(lookupTimeout: TimeSpan.FromSeconds(10), mutationTimeout: TimeSpan.FromSeconds(30), compute: new ThrowingCallbackCompute(rig.Gcp, reached));
        using var cts = new CancellationTokenSource();

        var pass = reconciler.EnforceLifecycleAsync(cts.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // MEASURED 2026-10-29 (PR #944 CI, Release, twice): the callee's throwing callback used to surface out of THIS call. Synchronous Cancel runs every relay inline, so it is deterministic.
        Should.NotThrow(() => cts.Cancel());

        IReadOnlyList<LifecycleOutcome> outcomes;
        try
        {
            outcomes = await pass;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        outcomes.ShouldNotContain(o => o.Action == LifecycleAction.Failed, "the user's cancel is not a cloud failure of that row");
    }

    private sealed class ListLog(List<DnaEntropyGraph.Core.Cloud.Audit.CloudAuditEntry> entries) : DnaEntropyGraph.Core.Cloud.Audit.ICloudAuditLog
    {
        public void Record(DnaEntropyGraph.Core.Cloud.Audit.CloudAuditEntry entry)
        {
            lock (entries)
            {
                entries.Add(entry);
            }
        }
    }

    [Fact]
    public async Task A_row_that_throws_does_not_end_the_pass_and_the_idle_sweep_still_runs()
    {
        var rig = new Rig();
        // A finished keep-alive run whose finish time makes the expiry arithmetic throw: the one unexpected error a single row can raise.
        await rig.SeedFinishedAsync("job-bad", AfterTaskAction.KeepAlive, keepAliveMinutes: 30, tweak: row => row with { FinishedAt = DateTimeOffset.MaxValue });
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = AfterTaskAction.Stop };
        var orphan = CloudJobRequestFactory.Create(options, "job-idle-orphan", new CloudScope(Project), "install-1", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
        await rig.Gcp.CreateVmAsync(orphan.Spec, Zone, CancellationToken.None);
        await rig.Gcp.StopVmAsync(orphan.Spec.VmName, Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromDays(10));

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-idle-orphan")).ShouldBeEmpty("the idle sweep ran after the bad row");
        outcomes.ShouldContain(o => o.JobId == "job-bad" && o.Action == LifecycleAction.Failed && o.ErrorCode == nameof(ArgumentOutOfRangeException));
    }

    [Fact]
    public async Task A_VM_labelled_for_another_installation_is_never_touched_even_under_this_installations_job_id()
    {
        var rig = new Rig();
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = AfterTaskAction.Delete };
        var foreign = CloudJobRequestFactory.Create(options, "job-x", new CloudScope(Project), "install-2", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
        await rig.Gcp.CreateVmAsync(foreign.Spec, Zone, CancellationToken.None);
        await rig.Env.Repo.UpsertAsync(
            new RunRecord("job-x", JobPhase.Completed, Launch.AddHours(-1), ProjectId: Project, OptionsJson: RunOptionsJson.Serialize(options), InstallationId: "install-1", FinishedAt: Launch.AddMinutes(-10)),
            CancellationToken.None);

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-x")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_row_of_another_installation_is_not_enforced()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-y", AfterTaskAction.Delete, tweak: row => row with { InstallationId = "install-2" });

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-y")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_idle_sweep_lists_a_project_once_when_a_row_with_no_account_and_the_selected_scope_name_the_same_project_and_account()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-legacy", AfterTaskAction.Stop); // the row names no account: it acts as the current one

        await rig.Reconciler(additionalSweepScopes: () => [new CloudScope("my-project", FakeGcp.DefaultAccountSub)]).EnforceLifecycleAsync(CancellationToken.None);

        rig.Gcp.CallsTo("ListByInstallation").Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_idle_sweep_lists_the_same_project_once_per_distinct_account()
    {
        var rig = new Rig(g => g.WithAccount("sub-x", "x@example.org", "my-project"));
        await rig.SeedFinishedAsync("job-x", AfterTaskAction.Stop, tweak: row => row with { AccountSub = "sub-x" });

        await rig.Reconciler(additionalSweepScopes: () => [new CloudScope("my-project", FakeGcp.DefaultAccountSub)]).EnforceLifecycleAsync(CancellationToken.None);

        rig.Gcp.CallsTo("ListByInstallation").Select(c => c.AccountSub).Order().ToList().ShouldBe([FakeGcp.DefaultAccountSub, "sub-x"]);
    }

    [Fact]
    public async Task A_finished_run_whose_account_is_signed_out_is_reported_as_needing_sign_in_and_another_accounts_resources_are_untouched()
    {
        var rig = new Rig(g => g.WithAccount("sub-a", "a@example.org", "project-a").WithAccountSignedOut("sub-a"));
        await rig.SeedFinishedAsync("job-a", AfterTaskAction.Delete, tweak: row => row with { ProjectId = "project-a", AccountSub = "sub-a" });
        await rig.SeedFinishedAsync("job-mine", AfterTaskAction.Delete); // the current account's own run, same pass
        var callsBefore = rig.Gcp.Calls.Count;

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        var signIn = outcomes.Single(o => o.JobId == "job-a");
        signIn.Action.ShouldBe(LifecycleAction.NeedsSignIn, signIn.ErrorCode);
        signIn.AccountSub.ShouldBe("sub-a");
        outcomes.Single(o => o.JobId == "job-mine").Action.ShouldBe(LifecycleAction.VmDeleted, "one account being signed out does not stop the pass");
        rig.Gcp.Calls.Skip(callsBefore).Where(c => c.AccountSub == "sub-a" && c.Method is "StopVm" or "DeleteVm").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_finished_run_whose_account_is_signed_out_tells_the_app_which_account_needs_to_sign_in()
    {
        // Issue #830 review M3.
        var rig = new Rig(g => g.WithAccount("sub-a", "a@example.org", "project-a").WithAccountSignedOut("sub-a"));
        await rig.SeedFinishedAsync("job-a", AfterTaskAction.Delete, tweak: row => row with { ProjectId = "project-a", AccountSub = "sub-a", ErrorCode = RunErrorCodes.LifecycleUnverified });
        var told = new List<string?>();

        await rig.Reconciler(onSignInNeeded: told.Add).EnforceLifecycleAsync(CancellationToken.None);

        told.Distinct().ShouldBe(["sub-a"]);
    }

    [Fact]
    public async Task A_signed_out_account_whose_finished_run_has_nothing_that_could_still_be_running_is_not_nagged_about_in_the_shell()
    {
        // Issue #830 review F4: a deliberately signed-out account with only a Completed run whose computer end was confirmed has nothing to protect.
        var rig = new Rig(g => g.WithAccount("sub-a", "a@example.org", "project-a").WithAccountSignedOut("sub-a"));
        await rig.SeedFinishedAsync("job-a", AfterTaskAction.Delete, tweak: row => row with { ProjectId = "project-a", AccountSub = "sub-a" });
        var told = new List<string?>();

        var outcomes = await rig.Reconciler(onSignInNeeded: told.Add).EnforceLifecycleAsync(CancellationToken.None);

        told.ShouldBeEmpty();
        outcomes.Single(o => o.JobId == "job-a").Action.ShouldBe(LifecycleAction.NeedsSignIn, "the outcome is still reported; only the shell bar is withheld");
    }

    [Fact]
    public async Task A_finished_run_whose_account_cannot_be_reached_is_deferred_not_told_to_sign_in_and_the_pass_goes_on()
    {
        // Issue #830 review M2: a lost connection is not an expired sign-in; the probe retries it.
        var rig = new Rig(g => g.WithAccount("sub-a", "a@example.org", "project-a").WithAccountFailure("sub-a", AuthErrorCodes.NetworkUnavailable));
        await rig.SeedFinishedAsync("job-a", AfterTaskAction.Delete, tweak: row => row with { ProjectId = "project-a", AccountSub = "sub-a" });
        await rig.SeedFinishedAsync("job-mine", AfterTaskAction.Delete);

        var reconciler = rig.Reconciler();
        var outcomes = await reconciler.EnforceLifecycleAsync(CancellationToken.None);

        var deferred = outcomes.Single(o => o.JobId == "job-a");
        deferred.Action.ShouldBe(LifecycleAction.Deferred, deferred.ErrorCode);
        deferred.ErrorCode.ShouldBe(AuthErrorCodes.NetworkUnavailable);
        outcomes.Single(o => o.JobId == "job-mine").Action.ShouldBe(LifecycleAction.VmDeleted);
    }

    [Fact]
    public async Task A_row_finished_long_ago_is_left_to_the_idle_sweep_not_looked_up()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-old", AfterTaskAction.Delete, tweak: row => row with { FinishedAt = Launch.AddDays(-30) });

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-old")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_something_already_drives_is_not_touched()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-busy", AfterTaskAction.Delete);
        var release = new TaskCompletionSource();
        var driver = rig.Env.Active.TryStart("job-busy", _ => release.Task);
        (driver is null).ShouldBeFalse();

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-busy")).Count.ShouldBe(1, "a job the engine or an earlier pass owns has one writer");
        outcomes.ShouldBeEmpty();
        release.SetResult();
        await driver;
    }

    [Fact]
    public async Task Without_a_connection_nothing_is_judged_and_the_pass_does_not_throw()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-off", AfterTaskAction.Delete);
        rig.Gcp.WithCloudNotConnected();

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        outcomes.ShouldContain(o => o.Action == LifecycleAction.Deferred);
        rig.Gcp.WithCloudConnected();
        (await rig.VmsAsync("job-off")).Count.ShouldBe(1);
    }

    // ---- 2. stopped VMs idle longer than the setting ----

    [Fact]
    public async Task The_default_idle_limit_is_72_hours_and_is_read_from_the_setting_when_one_is_set()
    {
        var rig = new Rig();
        CloudHousekeepingSettings.IdleStoppedVmLimit(rig.Env.Settings).ShouldBe(TimeSpan.FromHours(72));
        rig.Env.Settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = "6";
        CloudHousekeepingSettings.IdleStoppedVmLimit(rig.Env.Settings).ShouldBe(TimeSpan.FromHours(6));
        rig.Env.Settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = "0";
        CloudHousekeepingSettings.IdleStoppedVmLimit(rig.Env.Settings).ShouldBe(TimeSpan.FromHours(72), "zero is the default: there is no off switch");
        rig.Env.Settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = "soon";
        CloudHousekeepingSettings.IdleStoppedVmLimit(rig.Env.Settings).ShouldBe(TimeSpan.FromHours(72), "an unreadable value is the default, never off");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_stopped_VM_idle_past_the_limit_is_deleted_and_one_within_it_is_kept()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-idle", AfterTaskAction.Stop);
        await rig.StopAsync("job-idle");
        rig.Clock.Advance(TimeSpan.FromHours(48));
        await rig.SeedFinishedAsync("job-fresh", AfterTaskAction.Stop);
        await rig.StopAsync("job-fresh");
        rig.Clock.Advance(TimeSpan.FromHours(30));

        var outcomes = await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-idle")).ShouldBeEmpty("stopped 78 h ago, limit 72 h");
        (await rig.VmsAsync("job-fresh")).Count.ShouldBe(1, "stopped 30 h ago");
        outcomes.ShouldContain(o => o.JobId == "job-idle" && o.Action == LifecycleAction.IdleVmDeleted);
    }

    [Fact]
    public async Task The_idle_limit_setting_changes_what_is_deleted_and_zero_is_the_default_not_off()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-s", AfterTaskAction.Stop);
        await rig.StopAsync("job-s");
        rig.Clock.Advance(TimeSpan.FromHours(10));

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);
        (await rig.VmsAsync("job-s")).Count.ShouldBe(1, "10 h is inside the 72 h default");

        rig.Env.Settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = "0";
        rig.Clock.Advance(TimeSpan.FromDays(30));
        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);
        (await rig.VmsAsync("job-s")).ShouldBeEmpty("0 is the 72 h default, there is no off switch, and the VM sat stopped for 30 days");
    }

    [Fact]
    public async Task The_idle_sweep_reads_the_limit_from_the_setting()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-five", AfterTaskAction.Stop);
        await rig.StopAsync("job-five");
        rig.Clock.Advance(TimeSpan.FromHours(10));
        rig.Env.Settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = "5";

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-five")).ShouldBeEmpty("stopped 10 h ago, limit 5 h");
    }

    [Fact]
    public async Task The_idle_sweep_never_touches_a_VM_of_another_installation_or_one_without_labels_or_one_still_running()
    {
        var rig = new Rig();
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = AfterTaskAction.Stop };
        var foreign = CloudJobRequestFactory.Create(options, "job-other", new CloudScope(Project), "install-2", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
        await rig.Gcp.CreateVmAsync(foreign.Spec, Zone, CancellationToken.None);
        await rig.Gcp.StopVmAsync(foreign.Spec.VmName, Zone, CancellationToken.None);
        rig.Gcp.WithAlreadyExists("deg-unlabelled", Zone, "STOPPED");
        await rig.SeedFinishedAsync("job-running", AfterTaskAction.KeepAlive, keepAliveMinutes: 240);
        rig.Clock.Advance(TimeSpan.FromDays(10));
        rig.Env.Settings.Values[CloudHousekeepingSettings.IdleStoppedVmHoursKey] = "1";

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-other")).Count.ShouldBe(1, "another installation's VM");
        (await rig.Gcp.GetVmAsync("deg-unlabelled", Zone, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_idle_stopped_VM_whose_run_is_not_finished_is_left_to_that_run()
    {
        var rig = new Rig();
        await rig.Env.SeedAsync("job-live", JobPhase.Running, vm: true);
        await rig.StopAsync("job-live");
        rig.Clock.Advance(TimeSpan.FromDays(10));

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-live")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_idle_stopped_VM_with_no_history_row_is_deleted()
    {
        var rig = new Rig();
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = AfterTaskAction.Stop };
        var orphan = CloudJobRequestFactory.Create(options, "job-orphan", new CloudScope(Project), "install-1", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
        await rig.Gcp.CreateVmAsync(orphan.Spec, Zone, CancellationToken.None);
        await rig.Gcp.StopVmAsync(orphan.Spec.VmName, Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromDays(10));

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-orphan")).ShouldBeEmpty();
    }

    // ---- 3a. what the reconnect probe asks: is anything still deferred (issue #559) ----

    [Fact]
    public async Task HasDeferred_is_false_before_any_pass_true_after_an_offline_reattach_and_false_once_a_later_pass_judges_the_run()
    {
        var rig = new Rig(g => g.WithWorker(FakeWorkerMode.Done));
        await rig.Env.SeedAsync("job-hd", JobPhase.Running, vm: true);
        rig.Gcp.WithCloudNotConnected();
        var reconciler = rig.Reconciler();
        reconciler.HasDeferred.ShouldBeFalse("nothing has been asked yet");

        await reconciler.BeginReconcileAsync(CancellationToken.None);
        reconciler.HasDeferred.ShouldBeTrue("the run could not be judged offline");

        rig.Gcp.WithCloudConnected();
        await (await reconciler.BeginReconcileAsync(CancellationToken.None));
        rig.Env.Row("job-hd").Phase.ShouldBe(JobPhase.Completed, rig.Env.Row("job-hd").ErrorCode);
        reconciler.HasDeferred.ShouldBeFalse("judged now");
    }

    [Fact]
    public async Task HasDeferred_is_true_when_the_lifecycle_pass_could_not_ask_the_cloud_and_false_after_it_can()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-hl", AfterTaskAction.Delete);
        rig.Gcp.WithCloudNotConnected();
        var reconciler = rig.Reconciler();

        (await reconciler.EnforceLifecycleAsync(CancellationToken.None)).ShouldContain(o => o.Action == LifecycleAction.Deferred);
        reconciler.HasDeferred.ShouldBeTrue();

        rig.Gcp.WithCloudConnected();
        await reconciler.EnforceLifecycleAsync(CancellationToken.None);
        reconciler.HasDeferred.ShouldBeFalse();
    }

    [Fact]
    public async Task A_deferred_run_that_became_terminal_leaves_HasDeferred_at_the_next_pass()
    {
        var rig = new Rig(g => g.WithWorker(FakeWorkerMode.Done));
        await rig.Env.SeedAsync("job-gone", JobPhase.Running, vm: true);
        rig.Gcp.WithCloudNotConnected();
        var reconciler = rig.Reconciler();
        await reconciler.BeginReconcileAsync(CancellationToken.None);
        reconciler.HasDeferred.ShouldBeTrue("deferred offline");

        // The user cancels it while offline: the row is terminal and is no longer a candidate for any pass.
        await rig.Env.Repo.UpsertAsync(rig.Env.Row("job-gone") with { Phase = JobPhase.Cancelled, CreatedUtc = Launch.AddMinutes(-4) }, CancellationToken.None);
        rig.Gcp.WithCloudConnected();
        await (await reconciler.BeginReconcileAsync(CancellationToken.None));

        reconciler.HasDeferred.ShouldBeFalse("a run that ended is nothing to look at again");
    }

    [Fact]
    public async Task A_non_network_failure_judging_a_run_is_logged_and_is_not_deferred()
    {
        var rig = new Rig(g => g.WithWorker(FakeWorkerMode.Done));
        await rig.Env.SeedAsync("job-403", JobPhase.Running, vm: true);
        rig.Env.Images.Resolve(Arg.Any<string>(), Arg.Any<bool>()).Returns(_ => throw new InvalidOperationException("boom: secret-file-name.gb"));
        var reconciler = rig.Reconciler();

        var outcome = (await reconciler.ReattachAsync(CancellationToken.None)).Single();

        outcome.Action.ShouldBe(ReattachAction.Errored);
        outcome.ErrorCode.ShouldBe(nameof(InvalidOperationException));
        reconciler.HasDeferred.ShouldBeFalse("only a network failure keeps the reconnect probe alive");
        rig.Env.Log.Entries.ShouldContain(e => e.JobId == "job-403");
    }

    [Fact]
    public async Task A_lifecycle_pass_that_throws_a_non_network_error_clears_HasDeferred_and_still_throws()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-lc", AfterTaskAction.Delete);
        rig.Gcp.WithCloudNotConnected();
        var reconciler = rig.Reconciler();
        await reconciler.EnforceLifecycleAsync(CancellationToken.None);
        reconciler.HasDeferred.ShouldBeTrue("offline lifecycle pass");

        rig.Env.Repo.BeforeGetAll = (_, _) => throw new InvalidOperationException("database is busy");
        await Should.ThrowAsync<InvalidOperationException>(() => reconciler.EnforceLifecycleAsync(CancellationToken.None));

        reconciler.HasDeferred.ShouldBeFalse("a non-network failure must not keep the reconnect probe alive");
    }

    [Fact]
    public async Task Overlapping_passes_that_finish_out_of_order_leave_HasDeferred_as_the_latest_started_pass_found_it()
    {
        // Issue #559 review: the launch pass runs outside ReconcileOnReconnect's single-flight, so it can overlap an observer pass. The older pass,
        // finishing last with a network error, must not overwrite what the newer pass (which could ask the cloud) found.
        var rig = new Rig();
        var reconciler = rig.Reconciler();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Counting from here: the older pass's reattach read is 1 and its lifecycle read is 2 (parked, then times out).
        rig.Env.Repo.BeforeGetAll = async (call, _) =>
        {
            if (call == 2)
            {
                parked.TrySetResult();
                await release.Task;
                throw new TimeoutException();
            }
        };
        var older = reconciler.BeginReconcileAsync(CancellationToken.None);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await (await reconciler.BeginReconcileAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        reconciler.HasDeferred.ShouldBeFalse("precondition: the newer pass found nothing deferred");
        release.SetResult();
        await (await older); // the lifecycle failure is logged, not thrown (#575): the pass and the runs it reattached both end normally

        reconciler.HasDeferred.ShouldBeFalse("the older pass ended last but only the latest-started pass may write the flag");
    }

    [Fact]
    public async Task A_cancel_finish_that_never_returns_does_not_hold_the_pass_or_the_next_pass_back()
    {
        // Issue #559 review: FinishCancelAsync never marked its run judged, so one stuck cancel held the outer pass task (and, behind
        // ReconcileOnReconnect, the single-flight slot, so no later reconnect ran a pass).
        var rig = new Rig();
        await rig.Env.SeedAsync("job-stuck", JobPhase.Cancelling, vm: true);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        var shutdownToken = shutdown.Token;
        // The pass's own reads (the reattach listing and the lifecycle pass) use the shutdown token itself; the runner's cancel looks at the row with
        // the reattach driver's linked token, a different one. Parking by that, not by the read's number: the two run on different threads.
        rig.Env.Repo.BeforeGetAll = async (_, token) =>
        {
            if (token != shutdownToken && token.CanBeCanceled)
            {
                parked.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reconciler = rig.Reconciler();

        var inner = await reconciler.BeginReconcileAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        inner.IsCompleted.ShouldBeFalse("the cancel finish is still stuck");
        var second = await reconciler.BeginReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        second.IsCompleted.ShouldBeTrue("the next pass skips the run something already drives and ends");
        await shutdown.CancelAsync();
        var ended = await Record.ExceptionAsync(() => inner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        ended.ShouldBeAssignableTo<OperationCanceledException>("a shutdown ends the stuck cancel finish; it is neither a timeout nor a hang");
        reconciler.HasDeferred.ShouldBeFalse("a shutdown is not a missing connection: it must not arm the probe");
    }

    [Fact]
    public async Task A_network_failure_reading_the_run_table_for_the_reattach_alone_is_deferred()
    {
        // Issue #559 review r4 F1: only the reattach's own listing read times out (the lifecycle read succeeds), so neither a per-run nor a
        // lifecycle flag exists; the probe would never arm.
        var rig = new Rig();
        rig.Env.Repo.BeforeGetAll = (call, _) =>
        {
            if (call == 1)
            {
                throw new TimeoutException();
            }

            return Task.CompletedTask;
        };
        var reconciler = rig.Reconciler();

        await Should.ThrowAsync<TimeoutException>(async () => await reconciler.BeginReconcileAsync(CancellationToken.None));

        reconciler.HasDeferred.ShouldBeTrue("the reattach could not read the run table");

        await (await reconciler.BeginReconcileAsync(CancellationToken.None));
        reconciler.HasDeferred.ShouldBeFalse("a later pass read it");
    }

    [Fact]
    public async Task A_run_whose_cancel_finish_ends_deferred_stays_deferred_while_a_newer_pass_skips_it()
    {
        // Issue #559 review r4 F2: the newer pass is Skipped (the older pass drives the run); it must not clear the mark, and the older
        // pass's late Deferred must not be rejected for being older.
        var rig = new Rig();
        await rig.Env.SeedAsync("job-ow", JobPhase.Cancelling, vm: true);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        var shutdownToken = shutdown.Token;
        rig.Env.Repo.BeforeGetAll = async (_, token) =>
        {
            if (token != shutdownToken && token.CanBeCanceled)
            {
                parked.TrySetResult();
                await release.Task;
                throw new TimeoutException();
            }
        };
        var reconciler = rig.Reconciler();
        var older = await reconciler.BeginReconcileAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var newer = await reconciler.BeginReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await newer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.SetResult();
        await older.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        reconciler.HasDeferred.ShouldBeTrue("the run that drove the cancel finish ended Deferred; the pass that skipped it changes nothing");
    }

    [Fact]
    public async Task A_lifecycle_read_that_throws_does_not_orphan_the_runs_the_reattach_started()
    {
        // Issue #559 review r4 F3: the lifecycle step throws; the run the reattach already handed to a driver must still be tracked and waited for.
        var rig = new Rig(g => g.WithWorker(FakeWorkerMode.Done));
        await rig.Env.SeedAsync("job-orph", JobPhase.Running, vm: true);
        rig.Env.Repo.BeforeGetAll = (call, _) => call == 2 ? throw new InvalidOperationException("database is busy") : Task.CompletedTask;
        var reconciler = rig.Reconciler();
        using var observer = new ReconcileOnReconnect(new NullObserver(), () => reconciler);

        observer.OnConnectivityChanged(offline: false);
        await observer.WhenIdleAsync();

        var row = rig.Env.Row("job-orph");
        row.Phase.ShouldBe(JobPhase.Completed, row.ErrorCode);
    }

    [Fact]
    public async Task A_network_failure_judging_a_run_still_counts_as_deferred()
    {
        var rig = new Rig(g => g.WithWorker(FakeWorkerMode.Done));
        await rig.Env.SeedAsync("job-net", JobPhase.Running, vm: true);
        rig.Gcp.WithFindByJobIdFailure(new CloudError(null, null, "could not reach the service"), 1);
        var reconciler = rig.Reconciler();

        (await reconciler.ReattachAsync(CancellationToken.None)).Single().Action.ShouldBe(ReattachAction.Deferred);
        reconciler.HasDeferred.ShouldBeTrue();
    }

    // ---- 3. the reconnect trigger ----

    [Fact]
    public async Task Coming_back_online_judges_a_row_the_offline_pass_deferred_and_going_offline_does_not()
    {
        var rig = new Rig(g => g.WithWorker(FakeWorkerMode.Done));
        await rig.Env.SeedAsync("job-def", JobPhase.Running, vm: true);
        rig.Gcp.WithCloudNotConnected();
        var reconciler = rig.Reconciler();
        var observer = new ReconcileOnReconnect(new NullObserver(), () => reconciler);
        (await reconciler.ReattachAsync(CancellationToken.None)).Single().Action.ShouldBe(ReattachAction.Deferred);

        rig.Gcp.WithCloudConnected();
        observer.OnConnectivityChanged(offline: true);
        await observer.WhenIdleAsync();
        rig.Env.Row("job-def").Phase.ShouldBe(JobPhase.Running, "going offline judges nothing");

        observer.OnConnectivityChanged(offline: false);
        await observer.WhenIdleAsync();

        rig.Env.Row("job-def").Phase.ShouldBe(JobPhase.Completed, rig.Env.Row("job-def").ErrorCode);
    }

    [Fact]
    public async Task A_reconnect_while_a_run_is_active_does_not_drive_it_a_second_time()
    {
        var rig = new Rig(g => g.WithWorker(FakeWorkerMode.Done));
        await rig.Env.SeedAsync("job-act", JobPhase.Running, vm: true);
        var reconciler = rig.Reconciler();
        var observer = new ReconcileOnReconnect(new NullObserver(), () => reconciler);
        var release = new TaskCompletionSource();
        var started = 0;
        var bodyRunning = new TaskCompletionSource();
        var driver = rig.Env.Active.TryStart("job-act", async _ =>
        {
            Interlocked.Increment(ref started);
            bodyRunning.SetResult();
            await release.Task;
        });
        (driver is null).ShouldBeFalse();
        await bodyRunning.Task;

        observer.OnConnectivityChanged(offline: false);
        await observer.WhenIdleAsync();

        rig.Env.Row("job-act").Phase.ShouldBe(JobPhase.Running, "the pass skipped the run its owner is driving");
        rig.Gcp.CreateAttempts.ShouldBe(1, "only the seed created a VM");
        started.ShouldBe(1);
        release.SetResult();
        await driver;
    }

    [Fact]
    public async Task The_reconnect_observer_forwards_every_event_to_the_observer_it_wraps()
    {
        var inner = new NullObserver();
        var observer = new ReconcileOnReconnect(inner, (Func<JobReconciler>)(() => throw new InvalidOperationException("never resolved for these events")));

        observer.OnRetry(new CloudRetryEvent("op", 1, TimeSpan.Zero, 503, CloudErrorKind.Other, "m"));
        observer.OnTokenRefreshed("op");
        observer.OnConnectivityChanged(offline: true);

        inner.Retries.ShouldBe(1);
        inner.Refreshes.ShouldBe(1);
        inner.Changes.ShouldBe([true]);
        await observer.WhenIdleAsync();
    }

    [Fact]
    public async Task Reconnects_during_a_slow_pass_coalesce_into_one_follow_up_and_never_overlap()
    {
        var running = 0;
        var overlapped = false;
        var passes = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new ReconcileOnReconnect(new NullObserver(), async _ =>
        {
            var n = Interlocked.Increment(ref passes);
            if (Interlocked.Increment(ref running) > 1)
            {
                overlapped = true;
            }

            if (n == 1)
            {
                firstStarted.SetResult();
                await gate.Task;
            }

            Interlocked.Decrement(ref running);
            return Task.CompletedTask;
        });

        observer.OnConnectivityChanged(offline: false);
        await firstStarted.Task;
        observer.OnConnectivityChanged(offline: false);
        observer.OnConnectivityChanged(offline: false);
        observer.OnConnectivityChanged(offline: false);
        gate.SetResult();
        await observer.WhenIdleAsync();

        passes.ShouldBe(2, "three reconnects during pass 1 are one follow-up pass");
        overlapped.ShouldBeFalse("a pass started while another was still running");
    }

    [Fact]
    public async Task A_reconnect_after_the_passes_have_ended_starts_a_fresh_pass_and_a_faulting_pass_does_not_stop_later_ones()
    {
        var passes = 0;
        var log = new JobReconcilerTests.RecordingLog();
        var observer = new ReconcileOnReconnect(
            new NullObserver(),
            _ =>
            {
                Interlocked.Increment(ref passes);
                throw new InvalidOperationException("boom: secret-file-name.gb");
            },
            log);

        observer.OnConnectivityChanged(offline: false);
        await observer.WhenIdleAsync();
        observer.OnConnectivityChanged(offline: false);
        await observer.WhenIdleAsync();

        passes.ShouldBe(2);
        log.Entries.Count.ShouldBe(2, "a pass that throws leaves a trace");
        log.Entries.ShouldAllBe(e => e.ErrorClass == nameof(InvalidOperationException) && e.JobId == null);
        log.Entries.ShouldAllBe(e => !e.Source.Contains("secret") && !e.ErrorClass.Contains("secret"), "the message, which could carry a file name, is never logged");
    }

    // ---- 4. a long reattached run never holds the next pass back ----

    [Fact]
    public async Task A_pass_ends_when_its_reattached_runs_are_handed_off_not_when_they_end_so_a_second_reconnect_runs_the_lifecycle_and_sweep()
    {
        var rig = new Rig();
        rig.Env.ResultTimeout = TimeSpan.FromMinutes(5);
        await rig.Env.SeedAsync("job-long", JobPhase.Running, vm: true);
        var reconciler = rig.Reconciler();
        var observer = new ReconcileOnReconnect(new NullObserver(), () => reconciler);

        // Work for the first pass besides the run itself: a stopped delete-labelled VM and an idle stopped VM.
        await rig.SeedFinishedAsync("job-del-late", AfterTaskAction.Delete);
        await rig.StopAsync("job-del-late");
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = AfterTaskAction.Stop };
        var orphan = CloudJobRequestFactory.Create(options, "job-orphan-late", new CloudScope(Project), "install-1", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
        await rig.Gcp.CreateVmAsync(orphan.Spec, Zone, CancellationToken.None);
        await rig.Gcp.StopVmAsync(orphan.Spec.VmName, Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromDays(10));

        // The observer's FIRST pass (nothing else has touched job-long): it reattaches the run, whose worker never finishes, and must still
        // return (so the next reconnect is not coalesced behind it) while that run goes on.
        var delLateVm = (await rig.VmsAsync("job-del-late")).Single().Name;
        observer.OnConnectivityChanged(offline: false);
        await rig.Env.Active.WhenActiveAsync("job-long");
        await rig.Gcp.WhenVmDeletedAsync(delLateVm).WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken);
        await rig.Gcp.WhenVmDeletedAsync(orphan.Spec.VmName).WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken);
        rig.Env.Active.IsActive("job-long").ShouldBeTrue("the long run is still going while the lifecycle and the sweep already ran");

        // A second reconnect is a pass of its own, not a flag waiting for the long run to end.
        await rig.SeedFinishedAsync("job-del-later", AfterTaskAction.Delete);
        await rig.StopAsync("job-del-later");
        var delLaterVm = (await rig.VmsAsync("job-del-later")).Single().Name;
        observer.OnConnectivityChanged(offline: false);
        await rig.Gcp.WhenVmDeletedAsync(delLaterVm).WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken);
        rig.Env.Active.IsActive("job-long").ShouldBeTrue();

        // Stop the long run's driver so nothing outlives the test.
        await rig.Env.Active.CancelAsync("job-long", () => Task.CompletedTask);
    }

    [Fact]
    public async Task The_observer_pass_returns_while_a_reattached_run_is_held_open_and_a_later_reconnect_starts_a_second_pass()
    {
        var passes = 0;
        var heldRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPassStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPassStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new ReconcileOnReconnect(new NullObserver(), _ =>
        {
            var pass = Interlocked.Increment(ref passes);
            if (pass == 1)
            {
                firstPassStarted.SetResult();
            }
            else if (pass == 2)
            {
                secondPassStarted.SetResult();
            }

            return Task.FromResult<Task>(heldRun.Task);
        });

        observer.OnConnectivityChanged(offline: false);
        await firstPassStarted.Task.WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken);
        observer.OnConnectivityChanged(offline: false);
        await secondPassStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        heldRun.Task.IsCompleted.ShouldBeFalse("the run the first pass reattached is still going");
        passes.ShouldBe(2);
        heldRun.SetResult();
        await observer.WhenIdleAsync();
    }

    // ---- 4a. issue #575: a lifecycle pass that throws must not orphan the reattached runs ----

    private sealed class ThrowingSettings : ISettingsStore
    {
        public string? GetString(string key) => throw new InvalidOperationException("settings unreadable");

        public void SetString(string key, string value) => throw new InvalidOperationException("settings unreadable");
    }

    private static JobReconciler ReconcilerWhoseLifecycleThrows(Rig rig) => new(
        rig.Env.Runner,
        rig.Gcp,
        rig.Gcp,
        rig.Env.Repo,
        rig.Env.Inputs,
        rig.Env.Images,
        rig.Env.Active,
        new ThrowingSettings(),
        timeProvider: rig.Clock,
        log: rig.Env.Log);

    [Fact]
    public async Task When_the_lifecycle_pass_throws_the_reattached_runs_are_still_returned_and_the_failure_is_logged()
    {
        var rig = new Rig();
        rig.Env.ResultTimeout = TimeSpan.FromMinutes(5);
        await rig.Env.SeedAsync("job-orphan", JobPhase.Running, vm: true);
        var reconciler = ReconcilerWhoseLifecycleThrows(rig);

        var reattached = await reconciler.BeginReconcileAsync(CancellationToken.None);

        reattached.IsCompleted.ShouldBeFalse("the run the pass reattached is still going, and the caller holds the task for it");
        rig.Env.Active.IsActive("job-orphan").ShouldBeTrue("handed to its driver before the pass returned");
        rig.Env.Log.Entries.ShouldContain(e => e.Source == "reconciler" && e.ErrorClass == nameof(InvalidOperationException));
        await rig.Env.Active.CancelAsync("job-orphan", () => Task.CompletedTask);
        await reattached.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WhenIdleAsync_still_waits_for_a_run_reattached_by_a_pass_whose_lifecycle_step_threw()
    {
        var rig = new Rig();
        rig.Env.ResultTimeout = TimeSpan.FromMinutes(5);
        await rig.Env.SeedAsync("job-orphan2", JobPhase.Running, vm: true);
        var reconciler = ReconcilerWhoseLifecycleThrows(rig);
        var observer = new ReconcileOnReconnect(new NullObserver(), () => reconciler);

        observer.OnConnectivityChanged(offline: false);
        await rig.Env.Log.WhenLoggedAsync(e => e.Source == "reconciler").WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken);
        await rig.Env.Active.WhenActiveAsync("job-orphan2");
        var idle = observer.WhenIdleAsync();

        idle.IsCompleted.ShouldBeFalse("a run the pass reattached is still going");
        await rig.Env.Active.CancelAsync("job-orphan2", () => Task.CompletedTask);
        await idle.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_pass_hands_every_reattached_run_to_its_driver_before_it_returns()
    {
        // REGRESSION GUARD for #559's judged signal (it is green before any #575 change): two passes' reattach listings must not overlap, so a pass
        // ends only once its runs are registered as drivers. #575 item 4 asked for this and #559 already delivered it.
        var rig = new Rig();
        rig.Env.ResultTimeout = TimeSpan.FromMinutes(5);
        await rig.Env.SeedAsync("job-h1", JobPhase.Running, vm: true);
        await rig.Env.SeedAsync("job-h2", JobPhase.Running, vm: true);

        var reattached = await rig.Reconciler().BeginReconcileAsync(CancellationToken.None);

        rig.Env.Active.IsActive("job-h1").ShouldBeTrue();
        rig.Env.Active.IsActive("job-h2").ShouldBeTrue();
        await rig.Env.Active.CancelAsync("job-h1", () => Task.CompletedTask);
        await rig.Env.Active.CancelAsync("job-h2", () => Task.CompletedTask);
        await reattached.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_observer_does_not_keep_a_nested_wait_for_every_reconnect_pass_whose_runs_already_ended()
    {
        // Issue #575 item 5: each pass's reattached-runs task is dropped from what WhenIdleAsync waits on as soon as it ends. Every pass here hands
        // back a run group that is still going when it is tracked (completed later by the test), so each one is really added and must really be
        // removed: a group that is already complete when tracked never reaches the removal.
        var groups = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var passes = 0;
        var observer = new ReconcileOnReconnect(new NullObserver(), _ => Task.FromResult<Task>(groups[Interlocked.Increment(ref passes) - 1].Task));

        for (var i = 1; i <= 5; i++)
        {
            observer.OnConnectivityChanged(offline: false);
            await WhenTrackedAsync(observer, i);
        }

        observer.TrackedRunGroups.ShouldBe(5, "each pass's still-running group is tracked");

        // Out of order, and the last one held back: the tracked set follows the groups that are still going, one wait and not a chain.
        foreach (var i in new[] { 1, 3, 0, 4 })
        {
            groups[i].SetResult();
        }

        await WhenTrackedAsync(observer, 1);
        groups[2].SetResult();
        await observer.WhenIdleAsync();
        await WhenTrackedAsync(observer, 0);
    }

    /// <summary>Completes once the observer tracks exactly <paramref name="count"/> run groups: waits on <see cref="ReconcileOnReconnect.RunGroupsChanged"/> (subscribed before the first read), not on a poll.</summary>
    private static async Task WhenTrackedAsync(ReconcileOnReconnect observer, int count)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler()
        {
            if (observer.TrackedRunGroups == count)
            {
                reached.TrySetResult();
            }
        }

        observer.RunGroupsChanged += Handler;
        try
        {
            Handler();
            await reached.Task.WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken);
        }
        finally
        {
            observer.RunGroupsChanged -= Handler;
        }
    }

    [Fact]
    public async Task A_row_that_throws_is_logged_by_job_id_and_error_class_only()
    {
        var rig = new Rig();
        await rig.SeedFinishedAsync("job-bad-log", AfterTaskAction.KeepAlive, keepAliveMinutes: 30, tweak: row => row with { FinishedAt = DateTimeOffset.MaxValue });

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        rig.Env.Log.Entries.ShouldBe([("reconciler", "job-bad-log", nameof(ArgumentOutOfRangeException))]);
    }

    private sealed class NullObserver : ICloudCallObserver
    {
        public int Retries { get; private set; }

        public int Refreshes { get; private set; }

        public List<bool> Changes { get; } = [];

        public void OnRetry(CloudRetryEvent retry) => Retries++;

        public void OnTokenRefreshed(string operation) => Refreshes++;

        public void OnConnectivityChanged(bool offline) => Changes.Add(offline);
    }
}
