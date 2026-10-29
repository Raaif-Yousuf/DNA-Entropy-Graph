using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #938: a create that lands after a cancel's settle wait gave up is deleted when it lands, by name, scoped to THAT cancel of
/// THAT attempt; a create that answered while the label listing lags is still deleted; and nothing the cancel did not start (a later
/// attempt's VM of the same job id, a reused keep-alive VM) is ever touched by the late clean-up.
/// </summary>
public class LateCreateAfterCancelTests
{
    private const string Project = "my-project";
    private const string ZoneA = "us-central1-a";
    private const string ZoneB = "us-central1-b";

    private static VmSpec Spec(string jobId) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private sealed class Rig
    {
        public Rig(FakeGcp gcp, IComputeGateway? compute = null)
        {
            Gcp = gcp;
            Settings = new CloudRunSettings
            {
                CreateTimeout = TimeSpan.FromSeconds(30),
                CreateSettleTimeout = TimeSpan.FromMilliseconds(100),
                LifecycleTimeout = TimeSpan.FromSeconds(2),
                LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
                CancelAckTimeout = TimeSpan.Zero,
                OnLateCreate = outcome => Outcomes.Writer.TryWrite(outcome),
            };
            var calls = new GatewayCalls(Settings, gcp);
            var terminator = new VmTerminator(compute ?? gcp, calls, Settings);
            Provisioner = new VmProvisioner(compute ?? gcp, calls, Settings, terminator, JobIsFinished);
            Rows = new RunRowStore(Repo, null);
            Canceller = new VmCanceller(Rows, Provisioner, terminator, new GracefulCancel(gcp, gcp, calls, Settings), Settings);
        }

        public FakeGcp Gcp { get; }

        public CloudRunSettings Settings { get; }

        public InMemoryRunRepository Repo { get; } = new();

        public RunRowStore Rows { get; }

        public VmProvisioner Provisioner { get; }

        public VmCanceller Canceller { get; }

        public System.Threading.Channels.Channel<LateCreateOutcome> Outcomes { get; } = System.Threading.Channels.Channel.CreateUnbounded<LateCreateOutcome>();

        public Func<string, Task<bool>> JobIsFinished { get; set; } = _ => Task.FromResult(true);

        public Task SeedRowAsync(string jobId) => Repo.UpsertAsync(new RunRecord(jobId, JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);

        public RunRecord LastRow(string jobId) => Repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

        public async Task<LateCreateOutcome> NextOutcomeAsync()
            => await Outcomes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a create the caller then gives up on (its token is cancelled) while the request is still in flight inside the fake.</summary>
    private static async Task GiveUpOnABlockedCreateAsync(Rig rig, string jobId, string zone = ZoneA)
    {
        var spec = Spec(jobId);
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request(jobId, spec, zones: [zone]), spec, new RunProgress(), cts.Token);
        await rig.Gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);
    }

    [Fact]
    public async Task A_create_that_lands_after_the_settle_wait_is_deleted_by_name_and_the_cancel_record_says_so()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l1");
        await GiveUpOnABlockedCreateAsync(rig, "job-l1");

        await rig.Canceller.CancelAsync("job-l1", CancellationToken.None);

        rig.LastRow("job-l1").Phase.ShouldBe(JobPhase.Failed, "the VM end could not be confirmed inside the settle wait");
        rig.LastRow("job-l1").ErrorCode.ShouldBe(RunErrorCodes.CancelFailed);
        gcp.ReleaseCreate();
        var outcome = await rig.NextOutcomeAsync();

        outcome.ShouldBe(new LateCreateOutcome("job-l1", LateCreateState.Deleted));
        (await gcp.GetVmAsync("deg-job-l1", ZoneA, CancellationToken.None)).ShouldBeNull("the late VM billed until maxRunDuration");
        await WaitForAsync(() => rig.LastRow("job-l1").ErrorDetail?.Contains("was deleted", StringComparison.Ordinal) == true);
        rig.LastRow("job-l1").Phase.ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task A_late_delete_that_loses_a_race_with_another_delete_is_still_a_deleted_VM()
    {
        // Defect 2 of the failed first fix: a second delete's 404 (or any error once the VM reads gone) must not turn into a failure.
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true).WithNextDeleteRacingAWorkerDelete();
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l2");
        await GiveUpOnABlockedCreateAsync(rig, "job-l2");
        await rig.Canceller.CancelAsync("job-l2", CancellationToken.None);

        gcp.ReleaseCreate();
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.Deleted);
        (await gcp.GetVmAsync("deg-job-l2", ZoneA, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_late_delete_that_keeps_failing_is_bounded_and_reported_as_DeleteFailed()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true).WithDeleteFailure("deg-job-l3", CloudErrorKind.Permission);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l3");
        await GiveUpOnABlockedCreateAsync(rig, "job-l3");
        await rig.Canceller.CancelAsync("job-l3", CancellationToken.None);

        gcp.ReleaseCreate();
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.DeleteFailed);
        outcome.ErrorClass.ShouldBe("permission");
        rig.LastRow("job-l3").Phase.ShouldBe(JobPhase.Failed, "the cancel stays Failed: nothing deleted the VM");
    }

    [Fact]
    public async Task A_create_that_never_lands_inside_the_reap_window_is_given_up_on_and_reported()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var rig = new Rig(gcp);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(100);
        await rig.SeedRowAsync("job-l4");
        await GiveUpOnABlockedCreateAsync(rig, "job-l4");
        await rig.Canceller.CancelAsync("job-l4", CancellationToken.None);

        var outcome = await rig.NextOutcomeAsync();

        outcome.ShouldBe(new LateCreateOutcome("job-l4", LateCreateState.GaveUp));
        gcp.ReleaseCreate();
    }

    [Fact]
    public async Task A_create_that_answered_while_the_label_listing_lags_is_still_deleted_by_the_cancel()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l5");
        var spec = Spec("job-l5");
        var provisioned = await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-l5", spec), spec, new RunProgress(), CancellationToken.None);
        provisioned.Success.ShouldBeTrue();
        gcp.LabelListingBlind = true;

        await rig.Canceller.CancelAsync("job-l5", CancellationToken.None);

        (await gcp.GetVmAsync("deg-job-l5", ZoneA, CancellationToken.None)).ShouldBeNull("the cancel trusted a lagging label listing and left the VM the create had answered");
        rig.LastRow("job-l5").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_create_that_answered_and_whose_VM_later_deleted_itself_is_not_booked_as_live_and_nothing_is_deleted()
    {
        // Defect 1 of the failed first fix: the answer is a remembered descriptor, never refreshed.
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l6");
        var spec = Spec("job-l6");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-l6", spec), spec, new RunProgress(), CancellationToken.None)).Success.ShouldBeTrue();
        await gcp.DeleteVmAsync("deg-job-l6", ZoneA, CancellationToken.None); // the worker deleted itself (after-task Delete)
        var deletesBefore = gcp.DeleteAttempts.Count;

        await rig.Canceller.CancelAsync("job-l6", CancellationToken.None);

        gcp.DeleteAttempts.Count.ShouldBe(deletesBefore, "the cancel sent a delete for a VM that no longer exists");
        rig.LastRow("job-l6").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task A_later_VM_of_the_same_job_id_survives_the_clean_up_of_an_earlier_cancelled_create()
    {
        // Defect 3 of the failed first fix: per-job sets never cleared, so a Spot attempt 2 or a resume VM was deleted on arrival.
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l7");
        await GiveUpOnABlockedCreateAsync(rig, "job-l7");
        await rig.Canceller.CancelAsync("job-l7", CancellationToken.None);
        gcp.ReleaseCreate();
        (await rig.NextOutcomeAsync()).State.ShouldBe(LateCreateState.Deleted);

        var spec = Spec("job-l7");
        var second = await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-l7", spec, zones: [ZoneB]), spec, new RunProgress(), CancellationToken.None);

        second.Success.ShouldBeTrue();
        await Task.Delay(200, TestContext.Current.CancellationToken); // negative evidence: a wrongly kept per-job mark would delete on arrival
        (await gcp.GetVmAsync("deg-job-l7", ZoneB, CancellationToken.None)).ShouldNotBeNull("the second attempt's VM was deleted by the first cancel's clean-up");
        gcp.DeleteAttempts.Count(d => d.Zone == ZoneB).ShouldBe(0);
    }

    [Fact]
    public async Task A_second_attempt_that_starts_before_the_late_create_lands_does_not_get_its_VM_deleted()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l8");
        await GiveUpOnABlockedCreateAsync(rig, "job-l8");
        await rig.Canceller.CancelAsync("job-l8", CancellationToken.None);

        // The first create is still held. A new attempt for the same job id (another zone) creates its own VM once the gate opens.
        var spec = Spec("job-l8");
        var attempt = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-l8", spec, zones: [ZoneB]), spec, new RunProgress(), CancellationToken.None);
        // Real time on purpose: a window for the new attempt to reach its (held) create before the gate opens. Too short only weakens the check (the attempt adopts instead); it cannot fail correct code.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        gcp.ReleaseCreate();
        (await attempt.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Success.ShouldBeTrue();
        await rig.NextOutcomeAsync();

        gcp.DeleteAttempts.Select(d => d.Zone).Distinct().ShouldBe([ZoneA], "only the cancelled attempt's zone is ever deleted by the late clean-up");
    }

    [Fact]
    public async Task A_reuse_still_in_flight_when_the_cancel_gives_up_is_never_deleted_by_the_late_clean_up()
    {
        // Defect 4 of the failed first fix: a ReuseVmAsync answer names another run's keep-alive VM, which this run did not create.
        var gcp = new FakeGcp();
        var oldSpec = Spec("job-old");
        await gcp.CreateVmAsync(oldSpec, ZoneA, CancellationToken.None);
        await gcp.StopVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compute = new HeldReuseCompute(gcp, entered, gate);
        var rig = new Rig(gcp, compute);
        await rig.Repo.UpsertAsync(new RunRecord("job-old", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
        await rig.SeedRowAsync("job-l9");
        var spec = Spec("job-l9");

        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-l9", spec), spec, new RunProgress(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);
        await rig.Canceller.CancelAsync("job-l9", CancellationToken.None);
        gate.SetResult();
        await Task.Delay(300, TestContext.Current.CancellationToken); // negative evidence: a reap of the reuse would have deleted it by now

        gcp.DeleteAttempts.ShouldBeEmpty("the late clean-up deleted a VM this run did not create");
        rig.Outcomes.Reader.TryRead(out _).ShouldBeFalse("no late create was ever reported: the reuse is not a create");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            (DateTime.UtcNow < until).ShouldBeTrue("the condition never held");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private sealed class HeldReuseCompute(FakeGcp inner, TaskCompletionSource entered, TaskCompletionSource gate) : IComputeGateway
    {
        public async Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await gate.Task;
            return await inner.ReuseVmAsync(spec, existingVmName, zone, CancellationToken.None);
        }

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }
}
