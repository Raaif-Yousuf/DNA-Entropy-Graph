using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;
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

        public JobReconciler Reconciler(TimeSpan? lookupTimeout = null, TimeSpan? mutationTimeout = null) => Env.Reconciler(Clock, lookupTimeout, mutationTimeout);

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
    public async Task A_row_that_throws_does_not_end_the_pass_and_the_idle_sweep_still_runs()
    {
        var rig = new Rig();
        // A finished keep-alive run whose finish time makes the expiry arithmetic throw: the one unexpected error a single row can raise.
        await rig.SeedFinishedAsync("job-bad", AfterTaskAction.KeepAlive, keepAliveMinutes: 30, tweak: row => row with { FinishedAt = DateTimeOffset.MaxValue });
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = AfterTaskAction.Stop };
        var orphan = CloudJobRequestFactory.Create(options, "job-idle-orphan", Project, "install-1", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
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
        var foreign = CloudJobRequestFactory.Create(options, "job-x", Project, "install-2", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
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
        var foreign = CloudJobRequestFactory.Create(options, "job-other", Project, "install-2", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
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
        var orphan = CloudJobRequestFactory.Create(options, "job-orphan", Project, "install-1", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
        await rig.Gcp.CreateVmAsync(orphan.Spec, Zone, CancellationToken.None);
        await rig.Gcp.StopVmAsync(orphan.Spec.VmName, Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromDays(10));

        await rig.Reconciler().EnforceLifecycleAsync(CancellationToken.None);

        (await rig.VmsAsync("job-orphan")).ShouldBeEmpty();
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
        var observer = new ReconcileOnReconnect(inner, () => throw new InvalidOperationException("never resolved for these events"));

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
        var maxRunning = 0;
        var passes = 0;
        var gate = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource();
        var observer = new ReconcileOnReconnect(new NullObserver(), async _ =>
        {
            var n = Interlocked.Increment(ref passes);
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            if (n == 1)
            {
                firstStarted.SetResult();
                await gate.Task;
            }

            Interlocked.Decrement(ref running);
        });

        observer.OnConnectivityChanged(offline: false);
        await firstStarted.Task;
        observer.OnConnectivityChanged(offline: false);
        observer.OnConnectivityChanged(offline: false);
        observer.OnConnectivityChanged(offline: false);
        gate.SetResult();
        await observer.WhenIdleAsync();

        passes.ShouldBe(2, "three reconnects during pass 1 are one follow-up pass");
        maxRunning.ShouldBe(1, "two passes must never overlap");
    }

    [Fact]
    public async Task A_reconnect_after_the_passes_have_ended_starts_a_fresh_pass_and_a_faulting_pass_does_not_stop_later_ones()
    {
        var passes = 0;
        var observer = new ReconcileOnReconnect(new NullObserver(), _ =>
        {
            Interlocked.Increment(ref passes);
            throw new InvalidOperationException("boom");
        });

        observer.OnConnectivityChanged(offline: false);
        await observer.WhenIdleAsync();
        observer.OnConnectivityChanged(offline: false);
        await observer.WhenIdleAsync();

        passes.ShouldBe(2);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
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
