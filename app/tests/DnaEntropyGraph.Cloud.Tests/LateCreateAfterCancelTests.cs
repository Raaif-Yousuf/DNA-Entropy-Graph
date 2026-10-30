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
public partial class LateCreateAfterCancelTests
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
                LateCreateGrace = TimeSpan.FromMilliseconds(200),
                OnLateCreate = outcome => Outcomes.Writer.TryWrite(outcome),
            };
            var calls = new GatewayCalls(Settings, gcp);
            var terminator = new VmTerminator(compute ?? gcp, calls, Settings);
            Terminator = terminator;
            Provisioner = new VmProvisioner(compute ?? gcp, calls, Settings, terminator, JobIsFinished);
            Rows = new RunRowStore(Repo, null);
            Canceller = new VmCanceller(Rows, Provisioner, terminator, new GracefulCancel(gcp, gcp, calls, Settings), Settings);
        }

        public FakeGcp Gcp { get; }

        public VmTerminator Terminator { get; }

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
        await rig.Canceller.WhenReapsEndedAsync(); // the earlier cancel's clean-up has ended: nothing is left that could delete the new attempt's VM
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
        await gcp.WhenCreatesEnteredAsync(2); // the new attempt has reached its own (held) create
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
        (await rig.Provisioner.SettleInflightCreatesAsync("job-l9").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue("the reuse has landed"); // a signal, not a sleep
        await rig.Canceller.WhenReapsEndedAsync(); // no clean-up is running that could still delete it

        rig.LastRow("job-l9").ErrorDetail.ShouldNotBeNull().ShouldNotContain("The app deletes it", Case.Sensitive, "the app never deletes a reuse: the row must not promise it");
        gcp.DeleteAttempts.ShouldBeEmpty("the late clean-up deleted a VM this run did not create");
        rig.Outcomes.Reader.TryRead(out var reported).ShouldBeTrue("round 4 M3: the reuse is watched and stopped when it lands");
        reported!.State.ShouldBe(LateCreateState.ReuseStopped);
    }

    // ---- round 2: the gateway production has (a create that honours its token, but Compute had accepted the insert) ----

    /// <summary>Starts a create that honours its token, gives up on it, and returns once the run's ladder has ended Canceled.</summary>
    private static async Task CancelAnAcceptedCreateAsync(Rig rig, string jobId, string zone = ZoneA)
    {
        var spec = Spec(jobId);
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request(jobId, spec, zones: [zone]), spec, new RunProgress(), cts.Token);
        await rig.Gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);
    }

    [Fact]
    public async Task H1_an_accepted_insert_that_lands_after_the_cancel_looked_is_deleted_by_name_while_the_label_listing_lags()
    {
        var gcp = new FakeGcp().WithServerAcceptedCreateThatTheClientCancels(landAtCancel: false);
        gcp.LabelListingBlind = true;
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-h1");
        await CancelAnAcceptedCreateAsync(rig, "job-h1");

        await rig.Canceller.CancelAsync("job-h1", CancellationToken.None);
        (await gcp.GetVmAsync("deg-job-h1", ZoneA, CancellationToken.None)).ShouldBeNull("nothing had landed when the cancel looked");
        await gcp.LandAcceptedCreatesAsync();

        var outcome = await rig.NextOutcomeAsync();
        outcome.State.ToString().ShouldBe("Deleted", "the create task ended Canceled, yet Compute made the VM: it must still be found by its name and deleted");
        (await gcp.GetVmAsync("deg-job-h1", ZoneA, CancellationToken.None)).ShouldBeNull("the VM billed until maxRunDuration");
        await WaitForAsync(() => rig.LastRow("job-h1").ErrorDetail?.Contains("deleted", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task H1_an_accepted_insert_that_exists_when_the_cancel_looks_is_deleted_by_the_cancel_itself_even_with_a_blind_listing()
    {
        var gcp = new FakeGcp().WithServerAcceptedCreateThatTheClientCancels(landAtCancel: true);
        gcp.LabelListingBlind = true;
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-h1b");
        await CancelAnAcceptedCreateAsync(rig, "job-h1b");

        await rig.Canceller.CancelAsync("job-h1b", CancellationToken.None);

        (await gcp.GetVmAsync("deg-job-h1b", ZoneA, CancellationToken.None)).ShouldBeNull("the create was cancelled on the client side yet the VM exists: the cancel must find it by name");
        rig.LastRow("job-h1b").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task H1_a_create_that_ended_Canceled_and_never_made_a_VM_is_said_to_have_made_none_only_after_a_by_name_look_finds_nothing()
    {
        var gcp = new FakeGcp().WithServerAcceptedCreateThatTheClientCancels(landAtCancel: false);
        var rig = new Rig(gcp);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(300);
        await rig.SeedRowAsync("job-h1c");
        await CancelAnAcceptedCreateAsync(rig, "job-h1c");
        await rig.Canceller.CancelAsync("job-h1c", CancellationToken.None);

        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ToString().ShouldBe("CreateFailed");
        gcp.DeleteAttempts.ShouldBeEmpty("nothing existed to delete");
    }

    [Fact]
    public async Task M1_a_later_attempt_of_the_same_job_in_the_SAME_zone_keeps_the_VM_the_earlier_cancelled_create_made()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-m1");
        await GiveUpOnABlockedCreateAsync(rig, "job-m1", ZoneA);
        await rig.Canceller.CancelAsync("job-m1", CancellationToken.None);

        var spec = Spec("job-m1");
        var attempt = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-m1", spec, zones: [ZoneA]), spec, new RunProgress(), CancellationToken.None);
        await gcp.WhenCreatesEnteredAsync(2); // the new attempt has asked Compute for the same name in the same zone
        gcp.ReleaseCreate();
        (await attempt.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Success.ShouldBeTrue();
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ToString().ShouldBe("Superseded", "the VM now belongs to the newer attempt");
        (await gcp.GetVmAsync("deg-job-m1", ZoneA, CancellationToken.None)).ShouldNotBeNull("the new attempt's VM was deleted by the earlier cancel's clean-up");
        gcp.DeleteAttempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task M2_a_failing_by_name_look_never_skips_the_delete_of_a_VM_the_label_listing_found_and_does_not_fail_the_cancel()
    {
        // Round 5 M1: the PRODUCTION shape (the real gateway answers a delete of a missing VM with a silent success, so a delete proves nothing either way): the looks fail a few times, then read.
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-m2");
        var spec = Spec("job-m2");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-m2", spec), spec, new RunProgress(), CancellationToken.None)).Success.ShouldBeTrue();
        gcp.WithGetVmFailure(new CloudError(null, 503, "The service is currently unavailable."), 3);

        await rig.Canceller.CancelAsync("job-m2", CancellationToken.None);

        gcp.WithGetVmFailure(new CloudError(null, 503, "unused"), 0);
        (await gcp.GetVmAsync("deg-job-m2", ZoneA, CancellationToken.None)).ShouldBeNull("the VM the label listing found was left because a by-name look failed");
        rig.LastRow("job-m2").Phase.ShouldBe(JobPhase.Cancelled, "the VM is gone: an unreadable look is not a failed cancel");
        gcp.DeleteAttempts.Count.ShouldBe(1, "an unreadable look is not a reason to send the delete again: gone is decided only by a look that read");
    }

    [Fact]
    public async Task M2_with_the_label_listing_blind_and_every_by_name_look_failing_the_VM_is_still_deleted_by_name()
    {
        // Round 5 M1: production shape; the looks fail a few times and then read.
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-m2b");
        var spec = Spec("job-m2b");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-m2b", spec), spec, new RunProgress(), CancellationToken.None)).Success.ShouldBeTrue();
        gcp.LabelListingBlind = true;
        gcp.WithGetVmFailure(new CloudError(null, 503, "The service is currently unavailable."), 3);

        await rig.Canceller.CancelAsync("job-m2b", CancellationToken.None);

        gcp.WithGetVmFailure(new CloudError(null, 503, "unused"), 0);
        (await gcp.GetVmAsync("deg-job-m2b", ZoneA, CancellationToken.None)).ShouldBeNull("an unreadable VM is treated as present: it is deleted by name");
        rig.LastRow("job-m2b").Phase.ShouldBe(JobPhase.Cancelled);
        gcp.DeleteAttempts.Count.ShouldBe(1, "one delete by name went out while the look failed; the read-back looks again instead of deleting again");
    }

    [Fact]
    public async Task M2_the_late_watch_deletes_by_name_even_when_every_look_fails()
    {
        var gcp = new FakeGcp().WithServerAcceptedCreateThatTheClientCancels(landAtCancel: false);
        gcp.LabelListingBlind = true;
        var rig = new Rig(gcp);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(500);
        await rig.SeedRowAsync("job-m2d");
        await CancelAnAcceptedCreateAsync(rig, "job-m2d");
        gcp.WithGetVmFailure(new CloudError(null, 503, "The service is currently unavailable."), 100000);
        await rig.Canceller.CancelAsync("job-m2d", CancellationToken.None);

        await gcp.LandAcceptedCreatesAsync();
        var outcome = await rig.NextOutcomeAsync();

        gcp.WithGetVmFailure(new CloudError(null, 503, "unused"), 0);
        outcome.State.ShouldBe(LateCreateState.CouldNotConfirm, "no look ever saw the VM (round 4: an accepted delete proves nothing), so it cannot be said to have been deleted");
        (await gcp.GetVmAsync("deg-job-m2d", ZoneA, CancellationToken.None)).ShouldBeNull("an unreadable VM is treated as maybe present and deleted by name");
    }

    [Fact]
    public async Task M2_an_unreadable_VM_whose_delete_does_nothing_is_never_reported_as_gone()
    {
        var gcp = new FakeGcp().WithSilentNoOpDeletes();
        var rig = new Rig(gcp);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(50);
        await rig.SeedRowAsync("job-m2c");
        var spec = Spec("job-m2c");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-m2c", spec), spec, new RunProgress(), CancellationToken.None)).Success.ShouldBeTrue();
        gcp.LabelListingBlind = true;
        gcp.WithGetVmFailure(new CloudError(null, 503, "The service is currently unavailable."), 1000);

        await rig.Canceller.CancelAsync("job-m2c", CancellationToken.None);

        rig.LastRow("job-m2c").Phase.ShouldBe(JobPhase.Failed, "nobody confirmed the VM gone: an unreadable look is not a gone VM");
        rig.LastRow("job-m2c").ErrorCode.ShouldBe(RunErrorCodes.CancelFailed);
    }

    [Fact]
    public async Task La_a_run_that_ends_terminal_forgets_its_unclaimed_creates_but_never_the_ones_a_cancel_has_claimed()
    {
        var rig = new Rig(new FakeGcp());
        var spec = Spec("job-la");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-la", spec), spec, new RunProgress(), CancellationToken.None)).Success.ShouldBeTrue();
        rig.Provisioner.SentCount.ShouldBe(1, "the create was recorded when it was sent");

        var claimed = rig.Provisioner.ClaimSentCreates("job-la");
        rig.Provisioner.ForgetPendingRequests("job-la"); // the run's own terminal end, racing the cancel that holds the claim

        claimed.Count.ShouldBe(1);
        rig.Provisioner.SentCount.ShouldBe(1, "the forget took the snapshot a concurrent cancel was working from");
        rig.Provisioner.ClaimSentCreates("job-la").ShouldBeEmpty("an entry is handed to exactly one caller");
        rig.Provisioner.ReleaseSent(claimed[0]);
        var other = Spec("job-la2");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-la2", other), other, new RunProgress(), CancellationToken.None)).Success.ShouldBeTrue();
        rig.Provisioner.ForgetPendingRequests("job-la2");
        rig.Provisioner.SentCount.ShouldBe(0, "a run that ended on its own leaks nothing");
    }

    [Fact]
    public async Task Lc_the_late_outcome_never_reaches_a_newer_attempts_row()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-lc");
        await GiveUpOnABlockedCreateAsync(rig, "job-lc");
        await rig.Canceller.CancelAsync("job-lc", CancellationToken.None);
        var spec = Spec("job-lc");
        var attempt = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-lc", spec, zones: [ZoneB]), spec, new RunProgress(), CancellationToken.None);
        await gcp.WhenCreatesEnteredAsync(2);
        gcp.ReleaseCreate();
        (await attempt.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Success.ShouldBeTrue();

        (await rig.NextOutcomeAsync()).State.ShouldBe(LateCreateState.Deleted);

        (rig.LastRow("job-lc").ErrorDetail ?? string.Empty).ShouldNotContain("was deleted", Case.Sensitive, "the first attempt's clean-up wrote into a row that now belongs to a newer attempt");
    }

    [Fact]
    public async Task Lg_a_VM_the_listing_and_the_name_both_show_is_counted_once()
    {
        var gcp = new FakeGcp().WithSilentNoOpDeletes();
        var rig = new Rig(gcp);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(50);
        await rig.SeedRowAsync("job-lg");
        var spec = Spec("job-lg");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-lg", spec), spec, new RunProgress(), CancellationToken.None)).Success.ShouldBeTrue();

        await rig.Canceller.CancelAsync("job-lg", CancellationToken.None);

        rig.LastRow("job-lg").Phase.ShouldBe(JobPhase.Failed);
        rig.LastRow("job-lg").ErrorDetail.ShouldNotBeNull().ShouldStartWith("1 VM(s)", Case.Sensitive);
    }

    // ---- round 3 ----

    /// <summary>A create Compute answered with this error: no VM can come of it unless the kind says otherwise.</summary>
    private static CloudOperationException Refused(CloudErrorKind kind = CloudErrorKind.Quota, string message = "refused")
        => new(new CloudError(null, 403, message), kind);

    [Fact]
    public async Task H1_a_cancel_stops_but_never_deletes_a_VM_a_reuse_relabelled_to_this_job()
    {
        var gcp = new FakeGcp();
        var oldSpec = Spec("job-old2");
        await gcp.CreateVmAsync(oldSpec, ZoneA, CancellationToken.None);
        await gcp.StopVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-h1r");
        await gcp.ReuseVmAsync(Spec("job-h1r"), "deg-job-old2", ZoneA, CancellationToken.None); // the reuse landed before the cancel: the old VM now carries job-id=job-h1r

        await rig.Canceller.CancelAsync("job-h1r", CancellationToken.None);

        var vm = await gcp.GetVmAsync("deg-job-old2", ZoneA, CancellationToken.None);
        vm.ShouldNotBeNull("the cancel deleted a VM another run created and this run only borrowed");
        vm.Status.ShouldBe("STOPPED", "a borrowed VM is stopped, not left billing");
        gcp.DeleteAttempts.ShouldBeEmpty();
        rig.LastRow("job-h1r").Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task M1_a_create_that_answers_after_the_cancels_last_look_is_still_watched_and_deleted()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp);
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-m1x");
        await GiveUpOnABlockedCreateAsync(rig, "job-m1x");
        var fired = false;
        var findsBefore = compute.FindCalls;
        compute.AfterGetVm = async _ =>
        {
            if (compute.FindCalls >= findsBefore + 2 && !fired) // the cancel's own read-back listing has been taken: this is its last look
            {
                fired = true;
                gcp.ReleaseCreate(); // the create answers AFTER the last look the cancel will take
                await compute.Creates[0];
            }
        };

        await rig.Canceller.CancelAsync("job-m1x", CancellationToken.None);

        fired.ShouldBeTrue("the test never reached the cancel's last look");
        (await rig.NextOutcomeAsync()).State.ShouldBe(LateCreateState.Deleted);
        (await gcp.GetVmAsync("deg-job-m1x", ZoneA, CancellationToken.None)).ShouldBeNull("the cancel released a create it had never seen land");
    }

    [Fact]
    public async Task M2_a_later_attempt_whose_create_fails_does_not_take_ownership_and_the_earlier_late_VM_is_deleted()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp) { CreateOverride = (n, _, _) => n >= 2 ? Task.FromException<VmDescriptor>(Refused()) : null };
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-m2x");
        await GiveUpOnABlockedCreateAsync(rig, "job-m2x");
        await rig.Canceller.CancelAsync("job-m2x", CancellationToken.None);
        var spec = Spec("job-m2x");

        var second = await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-m2x", spec, zones: [ZoneA]), spec, new RunProgress(), CancellationToken.None);
        second.Success.ShouldBeFalse("the newer attempt's create was refused: it has no VM");
        gcp.ReleaseCreate();

        (await rig.NextOutcomeAsync()).State.ShouldBe(LateCreateState.Deleted);
        (await gcp.GetVmAsync("deg-job-m2x", ZoneA, CancellationToken.None)).ShouldBeNull("a create that was merely SENT took the earlier VM's ownership: it billed until maxRunDuration");
    }

    [Fact]
    public async Task M3_the_watch_does_not_delete_a_VM_a_newer_attempt_has_listed_and_is_about_to_adopt()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp);
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-m3");
        await GiveUpOnABlockedCreateAsync(rig, "job-m3");
        await rig.Canceller.CancelAsync("job-m3", CancellationToken.None);
        var findsBefore = compute.FindCalls;
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        compute.BeforeFind = n => n > findsBefore ? landed.Task : Task.CompletedTask;
        compute.AfterFind = async n =>
        {
            if (n > findsBefore)
            {
                listed.TrySetResult(); // the newer attempt holds a listing that names the late VM, and has not yet noted ownership
                await resume.Task;
            }
        };
        var spec = Spec("job-m3");
        var attempt = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-m3", spec, zones: [ZoneA]), spec, new RunProgress(), CancellationToken.None);

        gcp.ReleaseCreate();
        await compute.Creates[0]; // the late VM exists now
        landed.SetResult();
        await listed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var looks = compute.GetVmCalls;
        await WaitForAsync(() => compute.GetVmCalls >= looks + 5); // the watch has had several chances to delete it while the listing is held
        resume.SetResult();
        var result = await attempt.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        (await rig.NextOutcomeAsync()).State.ShouldBe(LateCreateState.Superseded);
        gcp.DeleteAttempts.ShouldBeEmpty("the watch deleted the VM the newer attempt had listed and adopted");
        (await gcp.GetVmAsync("deg-job-m3", ZoneA, CancellationToken.None)).ShouldNotBeNull();
    }

    /// <summary>A two-zone ladder whose first create is answered <paramref name="firstKind"/> and whose second is in flight (and ignores the cancel); the run is cancelled.</summary>
    private static async Task CancelATwoZoneLadderAsync(Rig rig, HookedCompute compute, string jobId)
    {
        _ = compute;
        var spec = Spec(jobId);
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request(jobId, spec, zones: [ZoneA, ZoneB]), spec, new RunProgress(), cts.Token);
        await rig.Gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);
        await rig.Canceller.CancelAsync(jobId, CancellationToken.None);
    }

    [Fact]
    public async Task M4_an_N_zone_ladder_writes_one_sentence_into_the_row_and_it_does_not_read_as_an_all_clear()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp) { CreateOverride = (n, _, _) => n == 1 ? Task.FromException<VmDescriptor>(Refused(CloudErrorKind.Other)) : null };
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-m4");
        await CancelATwoZoneLadderAsync(rig, compute, "job-m4");
        rig.LastRow("job-m4").Phase.ShouldBe(JobPhase.Failed);

        gcp.ReleaseCreate();
        await rig.NextOutcomeAsync();
        await rig.NextOutcomeAsync();
        await rig.Canceller.WhenReapsEndedAsync();

        var detail = rig.LastRow("job-m4").ErrorDetail.ShouldNotBeNull();
        (detail.Split("create request that was still in flight").Length - 1).ShouldBe(1, $"one sentence per cancel, not one per sent create: {detail}");
        detail.ShouldContain("still recorded as failed", Case.Sensitive, "a Failed row must not read as an all-clear");
        rig.LastRow("job-m4").Phase.ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task M5_creates_that_Compute_refused_are_not_watched_and_the_rest_share_one_loop_per_cancel()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp) { CreateOverride = (n, _, _) => n == 1 ? Task.FromException<VmDescriptor>(Refused(CloudErrorKind.Stockout)) : null };
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-m5");
        await CancelATwoZoneLadderAsync(rig, compute, "job-m5");

        gcp.ReleaseCreate();
        (await rig.NextOutcomeAsync()).State.ShouldBe(LateCreateState.Deleted);
        await rig.Canceller.WhenReapsEndedAsync();

        rig.Outcomes.Reader.TryRead(out var extra).ShouldBeFalse($"the stockout create was watched too: {extra}");
        rig.Terminator.ReapLoopsStarted.ShouldBe(1, "one polling loop per cancel, not one task per sent create (the counter counts loops that RUN, so a loop per create reads 2)");
    }

    [Fact]
    public async Task M5b_two_creates_of_unknown_outcome_are_polled_by_one_loop()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp) { CreateOverride = (n, _, _) => n == 1 ? Task.FromException<VmDescriptor>(Refused(CloudErrorKind.Other)) : null };
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-m5b");
        await CancelATwoZoneLadderAsync(rig, compute, "job-m5b");
        gcp.ReleaseCreate();
        await rig.NextOutcomeAsync();
        await rig.NextOutcomeAsync();
        await rig.Canceller.WhenReapsEndedAsync();

        rig.Terminator.ReapLoopsStarted.ShouldBe(1, "two creates of unknown outcome share one loop");
    }

    [Fact]
    public async Task L2_a_look_that_fails_and_a_delete_that_fails_say_the_app_could_not_confirm_not_that_a_VM_could_not_be_deleted()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true).WithDeleteFailure("deg-job-l2x", CloudErrorKind.Permission);
        var rig = new Rig(gcp);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(50);
        await rig.SeedRowAsync("job-l2x");
        await GiveUpOnABlockedCreateAsync(rig, "job-l2x");
        gcp.WithGetVmFailure(new CloudError(null, 503, "The service is currently unavailable."), 100000);
        await rig.Canceller.CancelAsync("job-l2x", CancellationToken.None);

        var outcome = await rig.NextOutcomeAsync();

        gcp.WithGetVmFailure(new CloudError(null, 503, "unused"), 0);
        outcome.State.ShouldBe(LateCreateState.CouldNotConfirm, "no look ever saw a VM: 'could not be deleted' says one existed");
        outcome.ErrorClass.ShouldBe("permission");
        (rig.LastRow("job-l2x").ErrorDetail ?? string.Empty).ShouldNotContain("could not be deleted", Case.Sensitive);
        gcp.ReleaseCreate();
    }

    [Fact]
    public async Task L4_a_VM_under_the_same_name_that_belongs_to_another_installation_is_never_deleted()
    {
        var gcp = new FakeGcp();
        var compute = new HookedCompute(gcp) { CreateOverride = (_, _, _) => Task.FromException<VmDescriptor>(Refused(CloudErrorKind.Other)) };
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-l4");
        var spec = Spec("job-l4");
        (await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-l4", spec, zones: [ZoneA]), spec, new RunProgress(), CancellationToken.None)).Success.ShouldBeFalse();
        await gcp.CreateVmAsync(spec with { InstallationId = "install-2" }, ZoneA, CancellationToken.None); // another installation's VM that carries this job id and name
        gcp.LabelListingBlind = true;

        await rig.Canceller.CancelAsync("job-l4", CancellationToken.None);
        await rig.Canceller.WhenReapsEndedAsync();

        (await gcp.GetVmAsync("deg-job-l4", ZoneA, CancellationToken.None)).ShouldNotBeNull("Hard Rule 9: a VM of another installation was deleted by name");
        gcp.DeleteAttempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task L5_the_attempt_bookkeeping_of_a_job_is_dropped_when_its_run_and_its_watch_have_ended()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var rig = new Rig(gcp);
        await rig.SeedRowAsync("job-l5x");
        await GiveUpOnABlockedCreateAsync(rig, "job-l5x");
        rig.Provisioner.LedgerCount.ShouldBeGreaterThan(0);
        await rig.Canceller.CancelAsync("job-l5x", CancellationToken.None);
        rig.Provisioner.LedgerCount.ShouldBeGreaterThan(0, "the watch still needs the attempt number");
        gcp.ReleaseCreate();
        await rig.NextOutcomeAsync();
        await rig.Canceller.WhenReapsEndedAsync();

        await WaitForAsync(() => rig.Provisioner.LedgerCount == 0);
    }

    [Fact]
    public async Task L6_a_failed_label_listing_puts_its_error_class_and_an_action_in_the_row_never_the_message()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var compute = new HookedCompute(gcp);
        var rig = new Rig(gcp, compute);
        await rig.SeedRowAsync("job-l6x");
        await GiveUpOnABlockedCreateAsync(rig, "job-l6x");
        var findsBefore = compute.FindCalls;
        compute.FindOverride = n => n == findsBefore + 1 ? throw Refused(CloudErrorKind.Permission, "SECRET-FILE-NAME.gb") : null;

        await rig.Canceller.CancelAsync("job-l6x", CancellationToken.None);

        var detail = rig.LastRow("job-l6x").ErrorDetail.ShouldNotBeNull();
        detail.ShouldNotContain("SECRET-FILE-NAME", Case.Sensitive);
        detail.ShouldContain("permission", Case.Insensitive);
        detail.ShouldContain("Cloud page", Case.Sensitive);
        gcp.ReleaseCreate();
    }

    private sealed class HookedCompute(FakeGcp inner) : IComputeGateway
    {
        private int _find;
        private int _get;
        private int _create;

        public int FindCalls => Volatile.Read(ref _find);

        public int GetVmCalls => Volatile.Read(ref _get);

        public List<Task<VmDescriptor>> Creates { get; } = [];

        /// <summary>Called with the 1-based create number; a non-null task is used INSTEAD of the real gateway.</summary>
        public Func<int, VmSpec, string, Task<VmDescriptor>?>? CreateOverride { get; set; }

        public Func<int, Task>? BeforeFind { get; set; }

        public Func<int, Task>? AfterFind { get; set; }

        /// <summary>Called with the 1-based Find number; may throw to fail it.</summary>
        public Func<int, object?>? FindOverride { get; set; }

        public Func<int, Task>? AfterGetVm { get; set; }

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _create);
            var task = CreateOverride?.Invoke(n, spec, zone) ?? inner.CreateVmAsync(spec, zone, cancellationToken);
            lock (Creates)
            {
                Creates.Add(task);
            }

            return task;
        }

        public async Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _get);
            var vm = await inner.GetVmAsync(vmName, zone, cancellationToken);
            if (AfterGetVm is { } after)
            {
                await after(n);
            }

            return vm;
        }

        public async Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _find);
            _ = FindOverride?.Invoke(n);
            if (BeforeFind is { } before)
            {
                await before(n);
            }

            var found = await inner.FindByJobIdAsync(jobId, cancellationToken);
            if (AfterFind is { } after)
            {
                await after(n);
            }

            return found;
        }

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
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
