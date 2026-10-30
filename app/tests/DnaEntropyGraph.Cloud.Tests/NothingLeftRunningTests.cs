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

    private sealed class ManualTimer(Action callback, TimeSpan due, TimeSpan period) : ITimer
    {
        public TimeSpan Due { get; } = due;

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
            var timer = new ManualTimer(() => callback(state), dueTime, period);
            lock (Timers)
            {
                Timers.Add(timer);
            }

            return timer;
        }

        /// <summary>Waits until a timeout timer with this due time has been armed (does not fire it).</summary>
        public async Task WaitForTimerAsync(TimeSpan due)
        {
            for (var i = 0; i < 400; i++)
            {
                lock (Timers)
                {
                    if (Timers.Any(t => !t.Disposed && t.Due == due))
                    {
                        return;
                    }
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException("No timer was armed.");
        }

        /// <summary>Waits for a timeout timer somebody armed (not yet disposed) and fires it: the clock reaching the deadline of a call that never answers.</summary>
        public async Task FireNextTimeoutAsync(TimeSpan? due = null)
        {
            for (var i = 0; i < 400; i++)
            {
                ManualTimer? pending;
                lock (Timers)
                {
                    pending = Timers.FirstOrDefault(t => !t.Disposed && t.Period == Timeout.InfiniteTimeSpan && (due is null || t.Due == due));
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
            Cleanup = new InstallationCleanup(Gcp, Gcp, Runs, clock: Clock);
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
            => new(scanner, cleanup, Settings, Dialog, Toasts, new EchoStrings(), NullDiagnosticsLog.Instance, Clock, Gcp);

        public async Task<VmDescriptor> CreateVmAsync(string installation, string job, TimeSpan maxRun)
        {
            var spec = new VmSpec("p", installation, job, "evo2_7b", "0.1.0", "stop", "g2-standard-8", maxRun, "DELETE");
            return await Gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        }

        public Task AddRunAsync(string job, JobPhase phase) => Runs.UpsertAsync(new RunRecord(job, phase, Start), CancellationToken.None);

        public JobPhase LatestPhase(string job) => Runs.AllRecordedInOrder.Last(r => r.JobId == job).Phase;

        public string? LatestErrorCode(string job) => Runs.AllRecordedInOrder.Last(r => r.JobId == job).ErrorCode;
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
        await stub.DidNotReceive().EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>());
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
        rig.LatestErrorCode("job-live").ShouldBe(RunErrorCodes.DeletedByCleanup);
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
        rig.LatestErrorCode("job-live").ShouldBe(RunErrorCodes.StoppedByCleanup);
        RunErrorCodes.All.ShouldContain(RunErrorCodes.StoppedByCleanup);
        RunErrorCodes.ResourceKey(RunErrorCodes.StoppedByCleanup).ShouldBe("RunError_cleanup_stopped");
        RunErrorCodes.ResourceKey(RunErrorCodes.StoppedByCleanup).ShouldNotBe(RunErrorCodes.ResourceKey(RunErrorCodes.DeletedByCleanup));
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
        failing.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("boom"));
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
    public async Task One_failed_scan_is_one_log_line_by_class()
    {
        var rig = new Rig();
        var log = Substitute.For<IDiagnosticsLog>();
        var broken = Substitute.For<IComputeGateway>();
        broken.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VmDescriptor>>>(_ => throw new IOException("offline"));
        var guard = new LeftRunningGuard(new LeftRunningScanner(broken, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(broken, rig.Gcp, rig.Runs), rig.Settings, rig.Dialog, rig.Toasts, new EchoStrings(), log, rig.Clock, rig.Gcp);

        (await guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();
        await Task.Yield();

        log.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IDiagnosticsLog.Warning)).ShouldBe(1);
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
    public async Task A_stop_request_that_is_never_accepted_keeps_the_window_open_and_names_the_failure()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Choice = 0;
        var hung = Forwarding(rig);
        hung.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource().Task);
        var guard = rig.NewGuard(new LeftRunningScanner(hung, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(hung, rig.Gcp, rig.Runs, clock: rig.Clock));

        var closing = guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Clock.FireNextTimeoutAsync(LeftRunningGuard.EndTimeout);

        (await closing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeFalse();
        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_StopFailedTitle")), Arg.Any<string>(), ToastSeverity.Error);
    }

    private static IComputeGateway Forwarding(Rig rig)
    {
        var stub = Substitute.For<IComputeGateway>();
        stub.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => rig.Gcp.ListByInstallationAsync(call.Arg<string>(), CancellationToken.None));
        stub.ListDisksByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => rig.Gcp.ListDisksByInstallationAsync(call.Arg<string>(), CancellationToken.None));
        stub.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(call => rig.Gcp.EndVmAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<bool>(2), call.ArgAt<Action?>(3), call.ArgAt<CancellationToken>(4)));
        return stub;
    }

    [Fact]
    public async Task A_driver_that_never_stops_does_not_hold_back_the_stop_request_or_the_run_rows_end()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        rig.Choice = 0;
        var active = new ActiveRuns();
        _ = active.TryStart("job-live", _ => new TaskCompletionSource().Task)!; // a driver that ignores its cancel
        var stub = Forwarding(rig);
        var guard = rig.NewGuard(new LeftRunningScanner(stub, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(stub, rig.Gcp, rig.Runs, active, clock: rig.Clock));

        var closing = guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Clock.FireNextTimeoutAsync(InstallationCleanup.DriverStopWait);

        (await closing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        await stub.Received(1).EndVmAsync(vm.Name, Zone, false, Arg.Any<Action?>(), Arg.Any<CancellationToken>());
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Cancelled);
        rig.LatestErrorCode("job-live").ShouldBe(RunErrorCodes.StoppedByCleanup);
    }

    [Fact]
    public async Task A_second_end_for_the_same_job_while_the_first_is_in_flight_is_done_not_a_failure_and_sends_nothing()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var active = new ActiveRuns();
        _ = active.TryStart("job-live", _ => new TaskCompletionSource().Task)!;
        var stub = Forwarding(rig);
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, active, clock: rig.Clock);
        var descriptor = (await rig.Gcp.ListByInstallationAsync(rig.Installation, CancellationToken.None)).Single();

        var first = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        await rig.Clock.WaitForTimerAsync(InstallationCleanup.DriverStopWait); // the first is now waiting on its driver, holding the job
        var second = await cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);

        second.ShouldBe(VmEndResult.Done);
        EndVmCalls(stub).ShouldBe(1); // the second sent nothing
        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None)).ShouldNotBeNull();
        await rig.Clock.FireNextTimeoutAsync(InstallationCleanup.DriverStopWait);
        (await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Done);
    }

    [Fact]
    public async Task A_cancel_some_other_code_owns_is_reported_as_already_ending_and_nothing_is_sent()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var active = new ActiveRuns();
        var ownerDone = new TaskCompletionSource();
        var owner = active.CancelAsync("job-live", () => ownerDone.Task); // the runner's own cancel, or "Delete everything"
        var stub = Forwarding(rig);
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, active, clock: rig.Clock);

        var result = await cleanup.EndVmAsync(rig.Installation, (await rig.Gcp.ListByInstallationAsync(rig.Installation, CancellationToken.None)).Single(), false, null, CancellationToken.None);

        result.ShouldBe(VmEndResult.AlreadyEnding);
        EndVmCalls(stub).ShouldBe(0);
        ownerDone.SetResult();
        await owner;
    }

    [Fact]
    public async Task A_computer_not_labelled_for_this_installation_is_refused_by_end_vm_and_nothing_is_sent()
    {
        var rig = new Rig();
        var theirs = new VmDescriptor("deg-job-x", Zone, "RUNNING", Labels: new Dictionary<string, string> { ["app"] = "dna-entropy-graph", ["installation-id"] = "inst-other" });
        var stub = Substitute.For<IComputeGateway>();
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, clock: rig.Clock);

        (await cleanup.EndVmAsync(rig.Installation, theirs, true, null, CancellationToken.None)).ShouldBe(VmEndResult.Failed);
        (await cleanup.EndVmAsync(rig.Installation, theirs, false, null, CancellationToken.None)).ShouldBe(VmEndResult.Failed);

        await stub.DidNotReceive().DeleteVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await stub.DidNotReceive().StopVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await stub.DidNotReceive().EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_abandoned_call_that_fails_later_is_observed_and_logged_by_class()
    {
        var rig = new Rig();
        var log = Substitute.For<IDiagnosticsLog>();
        var late = new TaskCompletionSource<IReadOnlyList<VmDescriptor>>();
        var hung = Substitute.For<IComputeGateway>();
        hung.ListByInstallationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(late.Task);
        var guard = new LeftRunningGuard(new LeftRunningScanner(hung, rig.Settings, rig.Runs, null, rig.Clock), new InstallationCleanup(hung, rig.Gcp, rig.Runs, clock: rig.Clock), rig.Settings, rig.Dialog, rig.Toasts, new EchoStrings(), log, rig.Clock, rig.Gcp);

        var closing = guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Clock.FireNextTimeoutAsync(LeftRunningGuard.ScanTimeout);
        (await closing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        late.SetException(new IOException("later"));

        for (var i = 0; i < 200 && log.ReceivedCalls().All(c => c.GetArguments().OfType<string>().All(a => a != "IOException")); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        log.Received().Warning(Arg.Any<string>(), Arg.Any<string?>(), Arg.Is<string>(c => c == "IOException"));
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("the condition never became true");
    }

    private static int EndVmCalls(IComputeGateway stub) => stub.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IComputeGateway.EndVmAsync));

    private static async Task<VmDescriptor> OnlyVmAsync(Rig rig) => (await rig.Gcp.ListByInstallationAsync(rig.Installation, CancellationToken.None)).Single();

    private static CloudOperationException Forbidden() => new(new CloudError("IAM_PERMISSION_DENIED", 403, "denied"), CloudErrorKind.Permission);

    [Fact]
    public async Task A_stop_google_accepted_but_has_not_finished_within_the_bound_reads_as_accepted_and_the_row_is_already_ended()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        rig.Gcp.HoldEndVmCompletion();
        rig.Choice = 0;

        var closing = rig.Guard.ConfirmExitAsync(CancellationToken.None);
        await WaitUntilAsync(() => rig.LatestPhase("job-live") == JobPhase.Cancelled); // written at acceptance, while the operation is still running
        await rig.Clock.FireNextTimeoutAsync(LeftRunningGuard.EndTimeout);

        (await closing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        rig.LatestErrorCode("job-live").ShouldBe(RunErrorCodes.StoppedByCleanup);
        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_StillEndingTitle")), Arg.Any<string>(), ToastSeverity.Info);
        rig.Toasts.DidNotReceive().ShowToast(Arg.Is<string>(t => t.StartsWith("LeftRunning_StopFailedTitle")), Arg.Any<string>(), ToastSeverity.Error);
        rig.Gcp.ReleaseEndVmCompletion();
    }

    [Fact]
    public async Task The_fake_reports_acceptance_while_the_vm_is_still_running_and_applies_the_stop_when_released()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-1", TimeSpan.FromMinutes(90));
        rig.Gcp.HoldEndVmCompletion();
        var accepted = false;

        var ending = rig.Gcp.EndVmAsync(vm.Name, Zone, false, () => accepted = true, CancellationToken.None);

        accepted.ShouldBeTrue();
        ending.IsCompleted.ShouldBeFalse();
        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
        rig.Gcp.ReleaseEndVmCompletion();
        await ending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None))!.Status.ShouldNotBe("RUNNING");
    }

    [Fact]
    public async Task A_refused_request_does_not_cancel_the_driver_and_leaves_the_row_running()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var active = new ActiveRuns();
        var driverCancelled = false;
        var driverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = active.TryStart("job-live", async ct =>
        {
            ct.Register(() => driverCancelled = true);
            driverStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        })!;
        await driverStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // its cancel callback is registered: a cancel is seen at once
        var refusing = Substitute.For<IComputeGateway>();
        refusing.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw Forbidden());
        var cleanup = new InstallationCleanup(refusing, rig.Gcp, rig.Runs, active, clock: rig.Clock);

        var result = await cleanup.EndVmAsync(rig.Installation, await OnlyVmAsync(rig), false, null, CancellationToken.None);

        result.ShouldBe(VmEndResult.Failed);
        driverCancelled.ShouldBeFalse();
        active.IsActive("job-live").ShouldBeTrue();
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Running);
    }

    [Fact]
    public async Task The_driver_is_cancelled_only_once_google_has_accepted_the_request()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var active = new ActiveRuns();
        var driverCancelled = false;
        var driverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = active.TryStart("job-live", async ct =>
        {
            ct.Register(() => driverCancelled = true);
            driverStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        })!;
        await driverStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // its cancel callback is registered: a cancel is seen at once
        Action? accept = null;
        var operation = new TaskCompletionSource();
        var stub = Substitute.For<IComputeGateway>();
        stub.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            accept = call.ArgAt<Action?>(3);
            return operation.Task;
        });
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, active, clock: rig.Clock);

        var ending = cleanup.EndVmAsync(rig.Installation, await OnlyVmAsync(rig), false, null, CancellationToken.None);
        await WaitUntilAsync(() => accept is not null);
        driverCancelled.ShouldBeFalse(); // the request is sent and not yet accepted

        accept!();
        await WaitUntilAsync(() => driverCancelled);
        operation.SetResult();
        (await ending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Done);
    }

    [Fact]
    public async Task A_caller_that_cancels_after_acceptance_still_gets_the_run_row_ended()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var active = new ActiveRuns();
        _ = active.TryStart("job-live", _ => new TaskCompletionSource().Task)!; // a driver that ignores its cancel
        rig.Gcp.HoldEndVmCompletion();
        var cleanup = new InstallationCleanup(rig.Gcp, rig.Gcp, rig.Runs, active, clock: rig.Clock);
        using var cts = new CancellationTokenSource();

        var ending = cleanup.EndVmAsync(rig.Installation, await OnlyVmAsync(rig), false, null, cts.Token);
        await rig.Clock.WaitForTimerAsync(InstallationCleanup.DriverStopWait); // accepted, waiting for the driver
        await cts.CancelAsync();
        await rig.Clock.FireNextTimeoutAsync(InstallationCleanup.DriverStopWait);

        (await ending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Done);
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Cancelled);
        rig.LatestErrorCode("job-live").ShouldBe(RunErrorCodes.StoppedByCleanup);
    }

    [Fact]
    public async Task A_second_stop_while_the_first_is_accepted_and_finishing_is_done_and_sends_nothing_more()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        rig.Gcp.HoldEndVmCompletion();
        var stub = Forwarding(rig);
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
        var descriptor = await OnlyVmAsync(rig);

        var first = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        await WaitUntilAsync(() => rig.LatestPhase("job-live") == JobPhase.Cancelled); // the first was accepted and is finishing on Google's side
        var second = await cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);

        second.ShouldBe(VmEndResult.Done);
        EndVmCalls(stub).ShouldBe(1);
        rig.Gcp.ReleaseEndVmCompletion();
        (await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Done);
    }

    [Fact]
    public async Task A_delete_asked_while_a_stop_is_in_flight_is_sent_and_the_row_names_the_delete()
    {
        var rig = new Rig();
        var vm = await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        rig.Gcp.HoldEndVmCompletion();
        var stub = Forwarding(rig);
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
        var descriptor = await OnlyVmAsync(rig);

        var stop = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        await WaitUntilAsync(() => rig.LatestPhase("job-live") == JobPhase.Cancelled);
        var delete = cleanup.EndVmAsync(rig.Installation, descriptor, true, null, CancellationToken.None);
        await WaitUntilAsync(() => rig.LatestErrorCode("job-live") == RunErrorCodes.DeletedByCleanup);

        await stub.Received(1).EndVmAsync(vm.Name, Zone, true, Arg.Any<Action?>(), Arg.Any<CancellationToken>());
        rig.Gcp.ReleaseEndVmCompletion();
        var results = await Task.WhenAll(stop, delete).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        results.ShouldAllBe(r => r == VmEndResult.Done);
        (await rig.Gcp.GetVmAsync(vm.Name, Zone, CancellationToken.None)).ShouldBeNull();
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_second_end_behind_a_first_that_is_never_accepted_fails_instead_of_claiming_it_is_finishing()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var stub = Substitute.For<IComputeGateway>();
        stub.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource().Task);
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
        var descriptor = await OnlyVmAsync(rig);

        _ = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        await WaitUntilAsync(() => EndVmCalls(stub) == 1);
        var second = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        await rig.Clock.FireNextTimeoutAsync(InstallationCleanup.InFlightWait);

        (await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Failed);
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Running);
    }

    private static void AcceptingStub(IComputeGateway stub, Func<bool, Task> operation)
        => stub.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.ArgAt<Action?>(3)?.Invoke();
            return operation(call.ArgAt<bool>(2));
        });

    [Fact]
    public async Task The_cancel_scope_is_released_once_the_row_is_ended_even_while_the_operation_is_still_running()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var active = new ActiveRuns();
        var stub = Substitute.For<IComputeGateway>();
        AcceptingStub(stub, _ => new TaskCompletionSource().Task); // accepted, and the operation never reaches DONE
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, active, clock: rig.Clock);

        _ = cleanup.EndVmAsync(rig.Installation, await OnlyVmAsync(rig), false, null, CancellationToken.None);
        await WaitUntilAsync(() => rig.LatestPhase("job-live") == JobPhase.Cancelled);
        await WaitUntilAsync(() =>
        {
            using var scope = active.TryBeginCancel("job-live"); // the runner's cancel, "Delete everything" and the reconciler wait on this marker
            return scope is not null;
        });
    }

    [Fact]
    public async Task A_stops_late_error_does_not_override_the_outcome_of_the_delete_that_superseded_it()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var stopOperation = new TaskCompletionSource();
        var stub = Substitute.For<IComputeGateway>();
        AcceptingStub(stub, delete => delete ? Task.CompletedTask : stopOperation.Task);
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
        var descriptor = await OnlyVmAsync(rig);

        var stop = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        await WaitUntilAsync(() => rig.LatestPhase("job-live") == JobPhase.Cancelled);
        var delete = await cleanup.EndVmAsync(rig.Installation, descriptor, true, null, CancellationToken.None);
        stopOperation.SetException(Forbidden()); // the stop's operation ends in error because the computer was deleted under it

        delete.ShouldBe(VmEndResult.Done);
        (await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Done);
    }

    [Fact]
    public async Task A_caller_cancelled_after_the_request_was_accepted_is_accepted_not_failed()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var stub = Substitute.For<IComputeGateway>();
        AcceptingStub(stub, _ => Task.FromCanceled(new CancellationToken(true))); // accepted, then the caller's token ended the wait for DONE
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
        var accepted = false;

        var result = await cleanup.EndVmAsync(rig.Installation, await OnlyVmAsync(rig), false, () => accepted = true, CancellationToken.None);

        result.ShouldBe(VmEndResult.Done);
        accepted.ShouldBeTrue();
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_caller_cancelled_before_the_request_was_accepted_throws_and_touches_nothing()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var stub = Substitute.For<IComputeGateway>();
        stub.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromCanceled(new CancellationToken(true)));
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
        var accepted = false;
        var descriptor = await OnlyVmAsync(rig);

        await Should.ThrowAsync<OperationCanceledException>(() => cleanup.EndVmAsync(rig.Installation, descriptor, false, () => accepted = true, CancellationToken.None));

        accepted.ShouldBeFalse();
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Running);
    }

    [Fact]
    public async Task Many_ends_for_one_job_started_at_once_send_one_request_and_none_is_told_someone_else_owns_it()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var descriptor = await OnlyVmAsync(rig);

        for (var round = 0; round < 40; round++)
        {
            var stub = Substitute.For<IComputeGateway>();
            var operation = new TaskCompletionSource();
            AcceptingStub(stub, _ => operation.Task);
            var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
            var go = new TaskCompletionSource();
            var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await go.Task;
                return await cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
            }, TestContext.Current.CancellationToken)).ToArray();

            go.SetResult();
            await WaitUntilAsync(() => calls.Count(c => c.IsCompleted) >= 7); // the seven that joined are answered while the owner's operation is still running
            operation.SetResult();
            var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            results.ShouldAllBe(r => r == VmEndResult.Done, $"round {round}");
            EndVmCalls(stub).ShouldBe(1, $"round {round}");
        }
    }

    [Fact]
    public async Task A_second_end_behind_a_first_that_fails_makes_its_own_attempt()
    {
        var rig = new Rig();
        await rig.CreateVmAsync(rig.Installation, "job-live", TimeSpan.FromMinutes(90));
        await rig.AddRunAsync("job-live", JobPhase.Running);
        var firstAttempt = new TaskCompletionSource();
        var stub = Substitute.For<IComputeGateway>();
        stub.EndVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(firstAttempt.Task, Task.CompletedTask);
        var cleanup = new InstallationCleanup(stub, rig.Gcp, rig.Runs, new ActiveRuns(), clock: rig.Clock);
        var descriptor = await OnlyVmAsync(rig);

        var first = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        await WaitUntilAsync(() => EndVmCalls(stub) == 1);
        var second = cleanup.EndVmAsync(rig.Installation, descriptor, false, null, CancellationToken.None);
        firstAttempt.SetException(Forbidden());

        (await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Failed);
        (await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(VmEndResult.Done);
        EndVmCalls(stub).ShouldBe(2);
        rig.LatestPhase("job-live").ShouldBe(JobPhase.Cancelled);
    }
}
