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
            Timers.Add(timer);
            return timer;
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
            ["LeftRunning_ToastBody"] = 2,
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
            Runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<IReadOnlyList<RunRecord>>(RunRows));
            var pricing = Substitute.For<IPricingSource>();
            pricing.Load().Returns(PricingTable.Parse(PricingJson));
            Scanner = new LeftRunningScanner(Gcp, Settings, Runs, pricing, Clock);
            Guard = new LeftRunningGuard(Scanner, Gcp, Dialog, Toasts, new EchoStrings(), NullDiagnosticsLog.Instance, Clock);
            Dialog.ChooseAsync(Arg.Any<string>(), Arg.Do<string>(m => Body = m), Arg.Do<IReadOnlyList<string>>(b => Buttons = b), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<int?>(Choice));
        }

        public ManualClock Clock { get; }

        public FakeGcp Gcp { get; }

        public MemorySettingsStore Settings { get; }

        public string Installation { get; }

        public IRunRepository Runs { get; } = Substitute.For<IRunRepository>();

        public List<RunRecord> RunRows { get; } = [];

        public IChoiceDialogService Dialog { get; } = Substitute.For<IChoiceDialogService>();

        public IToastService Toasts { get; } = Substitute.For<IToastService>();

        public LeftRunningScanner Scanner { get; }

        public LeftRunningGuard Guard { get; }

        public string? Body { get; private set; }

        public IReadOnlyList<string>? Buttons { get; private set; }

        public int? Choice { get; set; } = 2;

        public async Task<VmDescriptor> CreateVmAsync(string installation, string job, TimeSpan maxRun)
        {
            var spec = new VmSpec("p", installation, job, "evo2_7b", "0.1.0", "stop", "g2-standard-8", maxRun, "DELETE");
            return await Gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        }
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
        rig.Body.ShouldContain(vm.Name);
        rig.Body.ShouldContain("|60|"); // 90 minute limit, 30 minutes in
        rig.Body.ShouldContain("0.85"); // the hourly cost
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

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Another_installations_running_vm_is_never_named_or_touched()
    {
        var rig = new Rig();
        var theirs = await rig.CreateVmAsync("inst-other", "job-x", TimeSpan.FromMinutes(90));
        rig.Choice = 0;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        (await rig.Gcp.GetVmAsync(theirs.Name, Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
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
    public async Task Leave_running_touches_nothing_and_allows_exit()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Choice = 2;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
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
        var scanner = new LeftRunningScanner(failing, rig.Settings, rig.Runs, null, rig.Clock);
        var guard = new LeftRunningGuard(scanner, failing, rig.Dialog, rig.Toasts, new EchoStrings(), NullDiagnosticsLog.Instance, rig.Clock);

        (await guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeFalse();

        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_StopFailedTitle")), Arg.Is<string>(b => b.StartsWith("LeftRunning_StopFailedBody")), ToastSeverity.Error);
    }

    [Fact]
    public async Task A_scan_that_cannot_read_google_never_traps_the_user()
    {
        var rig = new Rig();
        var broken = Substitute.For<IComputeGateway>();
        broken.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VmDescriptor>>>(_ => throw new IOException("offline"));
        var scanner = new LeftRunningScanner(broken, rig.Settings, rig.Runs, null, rig.Clock);
        var guard = new LeftRunningGuard(scanner, broken, rig.Dialog, rig.Toasts, new EchoStrings(), NullDiagnosticsLog.Instance, rig.Clock);

        (await guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();
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
    public async Task The_five_minute_timer_scans_again_and_warns_once_about_a_vm_with_no_live_run()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-orphan", TimeSpan.FromMinutes(90));

        await rig.Guard.StartAsync(CancellationToken.None);

        rig.Clock.Timers.ShouldHaveSingleItem().Period.ShouldBe(TimeSpan.FromMinutes(5));
        // The launch scan already found it.
        rig.Toasts.Received(1).ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_ToastTitle")), Arg.Is<string>(b => b.Contains(vm.Name)), ToastSeverity.Warning);

        rig.Clock.Timers[0].Fire();
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
        rig.Clock.Timers[0].Fire();
        await rig.Guard.LastTickAsync;

        rig.Toasts.Received(1).ShowToast(Arg.Any<string>(), Arg.Is<string>(b => b.Contains(vm.Name)), ToastSeverity.Warning);
    }

    [Fact]
    public async Task A_vm_whose_run_is_still_live_is_not_warned_about_but_is_named_on_exit()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        rig.RunRows.Add(new RunRecord("job-live", JobPhase.Running, Start));

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

        rig.Clock.Timers[0].Disposed.ShouldBeTrue();
    }
}
