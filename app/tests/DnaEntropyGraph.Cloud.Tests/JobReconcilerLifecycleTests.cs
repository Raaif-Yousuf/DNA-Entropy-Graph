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
        await Record.ExceptionAsync(() => older); // ends in the TimeoutException, or (once a lifecycle failure is logged, not thrown, #575) normally

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
        // Read 1 is the reattach listing, 2 the lifecycle pass, 3 the runner's cancel looking at the row with the reattach's token.
        rig.Env.Repo.BeforeGetAll = async (call, token) =>
        {
            if (call == 3)
            {
                parked.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reconciler = rig.Reconciler();
        using var shutdown = new CancellationTokenSource();

        var inner = await reconciler.BeginReconcileAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        inner.IsCompleted.ShouldBeFalse("the cancel finish is still stuck");
        var second = await reconciler.BeginReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        second.IsCompleted.ShouldBeTrue("the next pass skips the run something already drives and ends");
        await shutdown.CancelAsync();
        await Record.ExceptionAsync(() => inner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
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
        var orphan = CloudJobRequestFactory.Create(options, "job-orphan-late", Project, "install-1", "0.1.0", null, [new StagedInput("seq.gb", "seq.gb")], "out");
        await rig.Gcp.CreateVmAsync(orphan.Spec, Zone, CancellationToken.None);
        await rig.Gcp.StopVmAsync(orphan.Spec.VmName, Zone, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromDays(10));

        // The observer's FIRST pass (nothing else has touched job-long): it reattaches the run, whose worker never finishes, and must still
        // return (so the next reconnect is not coalesced behind it) while that run goes on.
        observer.OnConnectivityChanged(offline: false);
        await WaitUntilAsync(() => Task.FromResult(rig.Env.Active.IsActive("job-long")));
        await WaitUntilAsync(async () => (await rig.VmsAsync("job-del-late")).Count == 0 && (await rig.VmsAsync("job-orphan-late")).Count == 0);
        rig.Env.Active.IsActive("job-long").ShouldBeTrue("the long run is still going while the lifecycle and the sweep already ran");

        // A second reconnect is a pass of its own, not a flag waiting for the long run to end.
        await rig.SeedFinishedAsync("job-del-later", AfterTaskAction.Delete);
        await rig.StopAsync("job-del-later");
        observer.OnConnectivityChanged(offline: false);
        await WaitUntilAsync(async () => (await rig.VmsAsync("job-del-later")).Count == 0);
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
        var observer = new ReconcileOnReconnect(new NullObserver(), _ =>
        {
            if (Interlocked.Increment(ref passes) == 2)
            {
                secondPassStarted.SetResult();
            }

            return Task.FromResult<Task>(heldRun.Task);
        });

        observer.OnConnectivityChanged(offline: false);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref passes) == 1));
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
        await WaitUntilAsync(() => Task.FromResult(rig.Env.Log.Entries.Any(e => e.Source == "reconciler") && rig.Env.Active.IsActive("job-orphan2")));
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
            await WaitUntilAsync(() => Task.FromResult(observer.TrackedRunGroups == i));
        }

        observer.TrackedRunGroups.ShouldBe(5, "each pass's still-running group is tracked");

        // Out of order, and the last one held back: the tracked set follows the groups that are still going, one wait and not a chain.
        foreach (var i in new[] { 1, 3, 0, 4 })
        {
            groups[i].SetResult();
        }

        await WaitUntilAsync(() => Task.FromResult(observer.TrackedRunGroups == 1));
        groups[2].SetResult();
        await observer.WhenIdleAsync();
        await WaitUntilAsync(() => Task.FromResult(observer.TrackedRunGroups == 0));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("the condition was not met within 10 s");
            await Task.Delay(10, TestContext.Current.CancellationToken);
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
