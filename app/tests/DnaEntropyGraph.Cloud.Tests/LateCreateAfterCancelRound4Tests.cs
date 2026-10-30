using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #938, round 4. H1: the real gateway answers a delete of a VM that does not exist (yet) with a silent success, so an accepted delete says nothing about whether the VM existed. M1: the
/// late outcome reaches the user and the log. M2: every delete of a job's VM holds the barrier a new attempt waits behind. M3: a reuse in flight at the cancel is stopped when it lands.
/// </summary>
public partial class LateCreateAfterCancelTests
{
    /// <summary>A gateway whose by-name look can be switched off: what a tripped breaker or a network blip does to GetVm.</summary>
    private sealed class FlakyLookCompute(FakeGcp inner) : IComputeGateway
    {
        public volatile bool FailGets;

        /// <summary>When set, the first delete that goes through switches the looks back on: the watch sent a delete while its looks failed, and the next look can read.</summary>
        public volatile bool RecoverAfterNextDelete;

        public async Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
            => FailGets ? throw new CloudOperationException(new CloudError("UNAVAILABLE", 503, "The service is currently unavailable."), CloudErrorKind.Network) : await inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        // The real gateway swallows Compute's 404 for a VM that is not there: the delete of a missing VM returns normally (GoogleComputeGateway.EndVmAsync).
        public async Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            await inner.DeleteVmAsync(vmName, zone, cancellationToken);
            if (RecoverAfterNextDelete)
            {
                FailGets = false;
            }
        }

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }

    [Fact]
    public async Task R4_H1_a_delete_sent_before_the_VM_exists_is_followed_by_another_when_a_look_finally_sees_it()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var flaky = new FlakyLookCompute(gcp) { FailGets = true };
        var rig = new Rig(gcp, flaky);
        rig.Settings.LifecycleTimeout = TimeSpan.FromSeconds(1);
        await rig.SeedRowAsync("job-r4a");
        await GiveUpOnABlockedCreateAsync(rig, "job-r4a");
        await rig.Canceller.CancelAsync("job-r4a", CancellationToken.None); // every look failed: the delete by name went to a VM that did not exist yet and "succeeded"

        flaky.RecoverAfterNextDelete = true;
        await WaitForAsync(() => !flaky.FailGets); // the watch has sent a delete, blind, while its looks failed; its looks read again from here
        gcp.ReleaseCreate(); // only now does the VM come to exist
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.Deleted, "a delete accepted before the VM existed was taken for 'the VM existed and is being deleted': nothing was ever sent again");
        (await gcp.GetVmAsync("deg-job-r4a", ZoneA, CancellationToken.None)).ShouldBeNull("the VM billed until maxRunDuration");
    }

    [Fact]
    public async Task R4_H1_an_accepted_delete_never_makes_the_outcome_Deleted_for_a_VM_no_look_ever_saw()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var flaky = new FlakyLookCompute(gcp) { FailGets = true };
        var rig = new Rig(gcp, flaky);
        rig.Settings.LifecycleTimeout = TimeSpan.FromSeconds(1);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(300);
        await rig.SeedRowAsync("job-r4b");
        await GiveUpOnABlockedCreateAsync(rig, "job-r4b");
        await rig.Canceller.CancelAsync("job-r4b", CancellationToken.None);
        flaky.RecoverAfterNextDelete = true;
        await WaitForAsync(() => !flaky.FailGets); // the watch sent a delete, blind, while its looks failed; the looks recover and find no VM, and the create never lands

        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldNotBe(LateCreateState.Deleted, "no look ever saw a VM, so 'a VM was deleted' is a claim nobody checked");
        outcome.State.ShouldBe(LateCreateState.GaveUp);
        gcp.ReleaseCreate();
    }

    [Fact]
    public void R4_H1_the_fake_deletes_a_missing_VM_silently_by_default_as_the_real_gateway_does()
    {
        var gcp = new FakeGcp();

        Should.NotThrow(() => gcp.DeleteVmAsync("deg-nothing", ZoneA, CancellationToken.None).GetAwaiter().GetResult());
    }

    // ---- M2: every delete of a job's VM holds the barrier ----

    [Fact]
    public async Task R4_M2_a_hold_on_a_jobs_deletes_makes_a_new_attempt_wait_until_it_is_released()
    {
        var ledger = new AttemptLedger();
        var hold = ledger.HoldDelete("job-r4m2");
        var attempt = ledger.BeginAttemptAsync("job-r4m2", CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        attempt.IsCompleted.ShouldBeFalse("a new attempt began while a delete of the job's VM was underway");

        hold.Dispose();

        (await attempt.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task R4_M2_a_create_whose_first_delete_ended_takes_the_barrier_again_for_its_next_delete()
    {
        var ledger = new AttemptLedger();
        var sent = new SentCreate("job-r4m2b", "deg-job-r4m2b", ZoneA, 0, "install-1");
        ledger.TryBeginDelete(sent).ShouldBe(DeleteDecision.Proceed);
        ledger.EndDelete(sent);

        ledger.TryBeginDelete(sent).ShouldBe(DeleteDecision.Proceed); // a later delete of the same VM (the watch after the cancel's own)
        var attempt = ledger.BeginAttemptAsync("job-r4m2b", CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        attempt.IsCompleted.ShouldBeFalse("the second delete ran without the barrier: a new attempt could adopt the VM being deleted");

        ledger.EndDelete(sent);

        await attempt.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>A gateway whose delete waits for a gate, so a test can look at the barrier while a delete is underway.</summary>
    private sealed class HeldDeleteCompute(FakeGcp inner, TaskCompletionSource entered, TaskCompletionSource gate) : IComputeGateway
    {
        public async Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await gate.Task;
            await inner.DeleteVmAsync(vmName, zone, CancellationToken.None);
        }

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }

    [Theory]
    [InlineData("cancel-retry")]
    [InlineData("run-page-delete")]
    [InlineData("end-after-failure")]
    public async Task R4_M2_every_way_the_app_deletes_a_jobs_VM_holds_the_barrier_a_new_attempt_waits_behind(string way)
    {
        var jobId = "job-r4m2-" + way;
        var gcp = new FakeGcp();
        var spec = Spec(jobId);
        await gcp.CreateVmAsync(spec, ZoneA, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new CloudRunSettings { LifecycleTimeout = TimeSpan.FromSeconds(5), LifecyclePollInterval = TimeSpan.FromMilliseconds(1) };
        var compute = new HeldDeleteCompute(gcp, entered, gate);
        var terminator = new VmTerminator(compute, new GatewayCalls(settings, gcp), settings);

        var delete = way switch
        {
            "cancel-retry" => terminator.DeleteOwnedVmsAndVerifyAsync(jobId, CreateSettleState.Settled, CancellationToken.None),
            "run-page-delete" => terminator.DeleteVmsAsync(jobId, CancellationToken.None),
            _ => terminator.EndVmAfterFailureAsync(TestInputs.Request(jobId, spec), ZoneA, forceDelete: true),
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var attempt = terminator.Ledger.BeginAttemptAsync(jobId, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        attempt.IsCompleted.ShouldBeFalse($"{way}: a new attempt began while the delete was underway and could adopt the VM being deleted");

        gate.SetResult();
        await delete.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await attempt.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    // ---- M3: a reuse in flight at the cancel is stopped when it lands ----

    [Fact]
    public async Task R4_M3_a_reuse_that_lands_after_the_cancel_is_stopped_never_deleted_and_the_row_does_not_say_the_app_will_not_stop_it()
    {
        var gcp = new FakeGcp();
        var oldSpec = Spec("job-old3");
        await gcp.CreateVmAsync(oldSpec, ZoneA, CancellationToken.None);
        await gcp.StopVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compute = new HeldReuseCompute(gcp, entered, gate);
        var rig = new Rig(gcp, compute);
        await rig.Repo.UpsertAsync(new RunRecord("job-old3", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
        await rig.SeedRowAsync("job-r4m3");
        var spec = Spec("job-r4m3");
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r4m3", spec), spec, new RunProgress(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);
        await rig.Canceller.CancelAsync("job-r4m3", CancellationToken.None);
        (rig.LastRow("job-r4m3").ErrorDetail ?? string.Empty).ShouldNotContain("does not stop it", Case.Sensitive);

        gate.SetResult();
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.ReuseStopped, "the reuse landed RUNNING (GPU billing) and nothing stopped it until the next launch");
        (await gcp.GetVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty("the VM is another run's: it is stopped, never deleted");
    }

    // ---- M1: the outcome reaches the user ----

    [Fact]
    public async Task R4_M1_a_late_VM_that_could_not_be_deleted_puts_a_code_the_Runs_page_shows_on_the_Cancelled_row()
    {
        var gcp = new FakeGcp().WithServerAcceptedCreateThatTheClientCancels(landAtCancel: false).WithDeleteFailure("deg-job-r4m1", CloudErrorKind.Permission);
        var rig = new Rig(gcp);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(100);
        await rig.SeedRowAsync("job-r4m1");
        await CancelAnAcceptedCreateAsync(rig, "job-r4m1");
        await rig.Canceller.CancelAsync("job-r4m1", CancellationToken.None);
        rig.LastRow("job-r4m1").Phase.ShouldBe(JobPhase.Cancelled);

        await gcp.LandAcceptedCreatesAsync();
        (await rig.NextOutcomeAsync()).State.ShouldBe(LateCreateState.DeleteFailed);
        await rig.Canceller.WhenReapsEndedAsync();

        rig.LastRow("job-r4m1").Phase.ShouldBe(JobPhase.Cancelled);
        rig.LastRow("job-r4m1").ErrorCode.ShouldBe(RunErrorCodes.LateCreateUnconfirmed, "ErrorDetail is for the diagnostics zip: the user only ever sees the code's message");
    }

    // ---- Lows ----

    [Fact]
    public async Task R4_Low_two_creates_of_unknown_outcome_are_polled_by_exactly_one_loop_and_both_are_in_it()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp) { CreateOverride = (n, _, _) => n == 1 ? Task.FromException<VmDescriptor>(Refused(CloudErrorKind.Other)) : null };
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-r4l");
        await CancelATwoZoneLadderAsync(rig, compute, "job-r4l");
        gcp.ReleaseCreate();
        await rig.NextOutcomeAsync();
        await rig.NextOutcomeAsync();
        await rig.Canceller.WhenReapsEndedAsync();

        rig.Terminator.ReapLoopsStarted.ShouldBe(1, "a loop per create would make this 2: the counter counts the loops that actually run");
    }

    [Fact]
    public async Task R4_Low_a_create_is_stamped_with_the_attempt_that_sent_it_not_the_newest_attempt()
    {
        var gcp = new FakeGcp();
        var compute = new HookedCompute(gcp);
        var firstListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        compute.AfterFind = n => n == 1 ? Hold() : Task.CompletedTask;
        async Task Hold()
        {
            firstListing.TrySetResult();
            await release.Task;
        }

        var rig = new Rig(gcp, compute);
        var spec = Spec("job-r4g");
        var first = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r4g", spec, zones: [ZoneA]), spec, new RunProgress(), CancellationToken.None);
        await firstListing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r4g", spec, zones: [ZoneB]), spec, new RunProgress(), CancellationToken.None);
        second.Success.ShouldBeTrue();
        release.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Success.ShouldBeTrue();

        var sent = rig.Provisioner.ClaimSentCreates("job-r4g");

        sent.Single(s => s.Zone == ZoneA).Generation.ShouldBe(1, "the first attempt's create was stamped with the second attempt's number, so the clean-up treats the first's VM as the second's own");
        sent.Single(s => s.Zone == ZoneB).Generation.ShouldBe(2);
    }
}
