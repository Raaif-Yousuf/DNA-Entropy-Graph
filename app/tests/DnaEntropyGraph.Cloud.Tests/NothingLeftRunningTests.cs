using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Presentation.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #113: the nothing-left-running scan (launch, exit, every 5 minutes). The wired observable: with FakeGcp holding one running labelled VM of this
/// installation, an exit request shows a modal that NAMES that VM with its minutes to its time limit, and the answer is carried out through the gateway.
/// </summary>
public class NothingLeftRunningTests
{
    private const string Zone = "us-central1-a";
    private const string PricingJson = """
        {
          "schema": 1, "asOf": "2026-10-01", "currency": "USD", "region": "us-central1", "source": "test",
          "disk": { "type": "pd-balanced", "usdPerGbMonth": 0.10 },
          "machines": { "g2-standard-8": { "accelerator": "1x NVIDIA L4", "onDemandUsdPerHour": 0.85, "spotUsdPerHour": 0.18 } }
        }
        """;

    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTimer(Action callback, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; } = period;

        public bool Disposed { get; private set; }

        public void Fire() => callback();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public List<ManualTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state), period);
            lock (Timers)
            {
                Timers.Add(timer);
            }

            return timer;
        }

        /// <summary>Waits for a timeout timer somebody armed (not yet disposed) and fires it: the clock reaching the deadline of a call that never answers.</summary>
        public async Task FireNextTimeoutAsync()
        {
            for (var i = 0; i < 400; i++)
            {
                ManualTimer? pending;
                lock (Timers)
                {
                    pending = Timers.FirstOrDefault(t => !t.Disposed && t.Period == Timeout.InfiniteTimeSpan);
                }

                if (pending is not null)
                {
                    pending.Fire();
                    return;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException("No timeout timer was armed.");
        }
    }

    /// <summary>Answers every key with <c>key|{0}|{1}...</c> (the key's real placeholder count), so a test sees which string was chosen and what was put in it.</summary>
    private sealed class EchoStrings : IStringResourceProvider
    {
        private static readonly Dictionary<string, int> Arity = new()
        {
            ["LeftRunning_ExitBody"] = 2,
            ["LeftRunning_VmLine"] = 4,
            ["LeftRunning_VmLineLive"] = 4,
            ["LeftRunning_VmLineNoLimit"] = 3,
            ["LeftRunning_CostPerHour"] = 1,
            ["LeftRunning_DiskNote"] = 2,
            ["LeftRunning_DiskNoteNoPrice"] = 1,
            ["LeftRunning_StopFailedBody"] = 1,
            ["LeftRunning_DeleteFailedBody"] = 1,
            ["LeftRunning_StillEndingBody"] = 1,
            ["LeftRunning_ToastBody"] = 2,
            ["LeftRunning_ToastBodyMany"] = 2,
            ["LeftRunning_ToastMore"] = 1,
        };

        public string GetString(string key) => key + string.Concat(Enumerable.Range(0, Arity.GetValueOrDefault(key)).Select(i => "|{" + i + "}"));
    }

    private sealed class Rig
    {
        public Rig()
        {
            Clock = new ManualClock();
            Gcp = new FakeGcp(Clock);
            Settings = new MemorySettingsStore();
            Installation = InstallationId.GetOrCreate(Settings);
            var pricing = Substitute.For<IPricingSource>();
            pricing.Load().Returns(PricingTable.Parse(PricingJson));
            Pricing = pricing;
            Scanner = new LeftRunningScanner(Gcp, Settings, Runs, pricing, Clock);
            Cleanup = new InstallationCleanup(Gcp, Gcp, Runs);
            Guard = NewGuard(Scanner, Cleanup);
            Dialog.ChooseAsync(Arg.Any<string>(), Arg.Do<string>(m => Body = m), Arg.Do<IReadOnlyList<string>>(b => Buttons = b), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<int?>(Choice));
        }

        public ManualClock Clock { get; }

        public FakeGcp Gcp { get; }

        public MemorySettingsStore Settings { get; }

        public string Installation { get; }

        public IPricingSource Pricing { get; }

        public InMemoryRunRepository Runs { get; } = new();

        public IChoiceDialogService Dialog { get; } = Substitute.For<IChoiceDialogService>();

        public IToastService Toasts { get; } = Substitute.For<IToastService>();

        public LeftRunningScanner Scanner { get; }

        public InstallationCleanup Cleanup { get; }

        public LeftRunningGuard Guard { get; }

        public string? Body { get; private set; }

        public IReadOnlyList<string>? Buttons { get; private set; }

        public int? Choice { get; set; } = 2;

        public LeftRunningGuard NewGuard(LeftRunningScanner scanner, InstallationCleanup cleanup)
            => new(scanner, cleanup, Settings, Dialog, Toasts, new EchoStrings(), NullDiagnosticsLog.Instance, Clock);

        public async Task<VmDescriptor> CreateVmAsync(string installation, string job, TimeSpan maxRun)
        {
            var spec = new VmSpec("p", installation, job, "evo2_7b", "0.1.0", "stop", "g2-standard-8", maxRun, "DELETE");
            return await Gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        }

        public Task AddRunAsync(string job, JobPhase phase) => Runs.UpsertAsync(new RunRecord(job, phase, Start), CancellationToken.None);

        public JobPhase LatestPhase(string job) => Runs.AllRecordedInOrder.Last(r => r.JobId == job).Phase;
    }

    [Fact]
    public async Task Exit_with_a_running_vm_shows_a_modal_naming_it_and_its_minutes_to_the_limit()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Clock.Now = Start.AddMinutes(30);

        var allowed = await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        allowed.ShouldBeTrue();
        rig.Body.ShouldNotBeNull();
        rig.Body!.ShouldContain(vm.Name);
        rig.Body!.ShouldContain("|60|"); // 90 minute limit, 30 minutes in
        rig.Body!.ShouldContain("0.85"); // the hourly cost
        rig.Buttons!.Count.ShouldBe(3);
        rig.Buttons[0].ShouldStartWith("LeftRunning_Stop");
        rig.Buttons[1].ShouldStartWith("LeftRunning_Delete");
        rig.Buttons[2].ShouldStartWith("LeftRunning_Leave");
    }

    [Fact]
    public async Task Exit_with_nothing_running_shows_no_modal_and_allows_exit()
    {
        var rig = new Rig();

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Another_installations_vm_and_disk_that_a_gateway_hands_back_unfiltered_are_never_named_or_touched()
    {
        var rig = new Rig();
        var labels = new Dictionary<string, string> { ["app"] = "dna-entropy-graph", ["installation-id"] = "inst-other", ["job-id"] = "job-x" };
        var theirs = new VmDescriptor("deg-job-x", Zone, "RUNNING", Labels: labels, CreatedAt: Start, MachineType: "g2-standard-8", MaxRunDuration: TimeSpan.FromMinutes(90));
        var stub = Substitute.For<IComputeGateway>();
        stub.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<VmDescriptor>>([theirs]));
        stub.ListDisksByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiskDescriptor>>([new DiskDescriptor("deg-job-x", Zone, 150, "READY", labels)]));
        var scanner = new LeftRunningScanner(stub, rig.Settings, rig.Runs, rig.Pricing, rig.Clock);
        var guard = rig.NewGuard(scanner, new InstallationCleanup(stub, rig.Gcp, rig.Runs));
        rig.Choice = 0;

        var report = await scanner.ScanAsync(CancellationToken.None);
        (await guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        report.IsClean.ShouldBeTrue();
        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await stub.DidNotReceive().StopVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await stub.DidNotReceive().DeleteVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_stops_the_vm_and_allows_exit()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Choice = 0;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None))!.Status.ShouldNotBe("RUNNING");
    }

    [Fact]
    public async Task Delete_deletes_the_vm_and_allows_exit()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Choice = 1;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_of_a_vm_with_a_live_run_ends_the_run_row_and_the_modal_warns_the_results_are_not_downloaded()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        rig.Choice = 1;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        rig.Body!.ShouldContain("LeftRunning_VmLineLive");
        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None)).ShouldBeNull();
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task Stop_of_a_vm_with_a_live_run_ends_the_run_row_too()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        rig.Choice = 0;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None))!.Status.ShouldNotBe("RUNNING");
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task Leave_running_touches_nothing_and_allows_exit()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        rig.Choice = 2;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Running);
    }

    [Fact]
    public async Task A_dialog_that_could_not_be_shown_still_lets_the_app_close()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Choice = null;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_stop_that_fails_keeps_the_app_open_and_names_the_action()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Choice = 0;
        var failing = Substitute.For<IComputeGateway>();
        failing.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => rig.Gcp.ListByInstallationAsync(call.Arg<string>(), CancellationToken.None));
        failing.ListDisksByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => rig.Gcp.ListDisksByInstallationAsync(call.Arg<string>(), CancellationToken.None));
        failing.StopVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("boom"));
        var guard = rig.NewGuard(new LeftRunningScanner(failing, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(failing, rig.Gcp, rig.Runs));

        (await guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeFalse();

        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_StopFailedTitle")), Arg.Is<string>(b => b.StartsWith("LeftRunning_StopFailedBody")), ToastSeverity.Error);
    }

    [Fact]
    public async Task A_scan_that_cannot_read_google_never_traps_the_user()
    {
        var rig = new Rig();
        var broken = Substitute.For<IComputeGateway>();
        broken.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VmDescriptor>>>(_ => throw new IOException("offline"));
        var guard = rig.NewGuard(new LeftRunningScanner(broken, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(broken, rig.Gcp, rig.Runs));

        (await guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_listing_that_never_answers_times_out_and_lets_the_app_close()
    {
        var rig = new Rig();
        var hung = Substitute.For<IComputeGateway>();
        hung.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<IReadOnlyList<VmDescriptor>>().Task);
        var guard = rig.NewGuard(new LeftRunningScanner(hung, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(hung, rig.Gcp, rig.Runs));

        var closing = guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Clock.FireNextTimeoutAsync();

        (await closing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_that_never_answers_says_it_will_finish_on_googles_side_and_lets_the_app_close()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Choice = 0;
        var hung = Substitute.For<IComputeGateway>();
        hung.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => rig.Gcp.ListByInstallationAsync(call.Arg<string>(), CancellationToken.None));
        hung.ListDisksByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => rig.Gcp.ListDisksByInstallationAsync(call.Arg<string>(), CancellationToken.None));
        hung.StopVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource().Task);
        var guard = rig.NewGuard(new LeftRunningScanner(hung, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(hung, rig.Gcp, rig.Runs));

        var closing = guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Clock.FireNextTimeoutAsync();

        (await closing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_StillEndingTitle")), Arg.Is<string>(b => b.StartsWith("LeftRunning_StillEndingBody")), ToastSeverity.Info);
    }

    [Fact]
    public async Task A_dialog_gate_that_never_frees_gives_no_answer_within_the_bound_the_guard_asks_for()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.Received(1).ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), LeftRunningGuard.DialogGateTimeout, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stopped_disks_are_reported_with_their_monthly_cost()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        await rig.Gcp.StopVmAsync(vm.Name, Zone, CancellationToken.None);
        rig.Gcp.WithDisk(new DiskDescriptor(vm.Name, Zone, 150, "READY", new Dictionary<string, string> { ["app"] = "dna-entropy-graph", ["installation-id"] = rig.Installation }, AttachedTo: [vm.Name]));

        var report = await rig.Scanner.ScanAsync(CancellationToken.None);

        report.RunningVms.ShouldBeEmpty();
        var disk = report.StoppedDisks.ShouldHaveSingleItem();
        disk.UsdPerMonth.ShouldNotBeNull();
        disk.UsdPerMonth!.Value.ShouldBe(disk.SizeGb * 0.10, 0.0001);
    }

    [Fact]
    public async Task A_vm_with_no_known_time_limit_gets_the_neutral_line_not_a_claim_it_ends_itself()
    {
        var rig = new Rig();
        var labels = new Dictionary<string, string> { ["app"] = "dna-entropy-graph", ["installation-id"] = rig.Installation, ["job-id"] = "job-n" };
        var noLimit = new VmDescriptor("deg-job-n", Zone, "RUNNING", Labels: labels, CreatedAt: Start, MachineType: "g2-standard-8");
        var stub = Substitute.For<IComputeGateway>();
        stub.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<VmDescriptor>>([noLimit]));
        stub.ListDisksByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DiskDescriptor>>([]));
        var guard = rig.NewGuard(new LeftRunningScanner(stub, rig.Settings, rig.Runs, rig.Pricing, rig.Clock), new InstallationCleanup(stub, rig.Gcp, rig.Runs));

        await guard.ConfirmExitAsync(CancellationToken.None);

        rig.Body!.ShouldContain("LeftRunning_VmLineNoLimit|deg-job-n");
    }

    [Fact]
    public async Task The_five_minute_timer_scans_again_and_warns_once_about_a_vm_with_no_live_run()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-orphan", TimeSpan.FromMinutes(90));

        await rig.Guard.StartAsync(CancellationToken.None);

        rig.Clock.Timers.Single(t => t.Period == LeftRunningGuard.Period);
        // The launch scan already found it.
        rig.Toasts.Received(1).ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_ToastTitle")), Arg.Is<string>(b => b.Contains(vm.Name)), ToastSeverity.Warning);

        rig.Clock.Timers.Single(t => t.Period == LeftRunningGuard.Period).Fire();
        await rig.Guard.LastTickAsync;

        // Same VM, no second toast.
        rig.Toasts.ReceivedWithAnyArgs(1).ShowToast(default!, default!, default);
    }

    [Fact]
    public async Task A_tick_finds_a_vm_that_appeared_after_launch()
    {
        var rig = new Rig();
        await rig.Guard.StartAsync(CancellationToken.None);
        rig.Toasts.ReceivedWithAnyArgs(0).ShowToast(default!, default!, default);

        var vm = await rig.CreateVmAsync(rig.Installation, "job-late", TimeSpan.FromMinutes(90));
        rig.Clock.Timers.Single(t => t.Period == LeftRunningGuard.Period).Fire();
        await rig.Guard.LastTickAsync;

        rig.Toasts.Received(1).ShowToast(Arg.Any<string>(), Arg.Is<string>(b => b.Contains(vm.Name)), ToastSeverity.Warning);
    }

    [Fact]
    public async Task Several_vms_found_in_one_scan_make_one_toast_naming_the_count_and_the_first_names()
    {
        var rig = new Rig();
        var names = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            names.Add((await rig.CreateVmAsync(rig.Installation, $"job-{i}", TimeSpan.FromMinutes(90))).Name);
        }

        await rig.Guard.StartAsync(CancellationToken.None);

        var calls = rig.Toasts.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IToastService.ShowToast)).ToList();
        calls.Count.ShouldBe(1);
        var body = (string)calls[0].GetArguments()[1]!;
        body.ShouldStartWith("LeftRunning_ToastBodyMany|5|");
        body.ShouldContain(names.OrderBy(n => n, StringComparer.Ordinal).First());
        body.ShouldContain("LeftRunning_ToastMore|2");
    }

    [Fact]
    public async Task A_vm_whose_run_is_still_live_is_not_warned_about_but_is_named_on_exit()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);

        await rig.Guard.StartAsync(CancellationToken.None);
        rig.Toasts.ReceivedWithAnyArgs(0).ShowToast(default!, default!, default);

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);
        rig.Body!.ShouldContain(vm.Name);
        rig.Body!.ShouldContain("LeftRunning_VmLineLive");
    }

    [Fact]
    public async Task Disposing_the_guard_stops_the_timer()
    {
        var rig = new Rig();
        await rig.Guard.StartAsync(CancellationToken.None);

        rig.Guard.Dispose();

        rig.Clock.Timers.Single(t => t.Period == LeftRunningGuard.Period).Disposed.ShouldBeTrue();
    }
}
