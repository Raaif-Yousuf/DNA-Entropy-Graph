using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #938, round 5. H1: a reuse relabelled but not yet started is watched until its start answers. L1: a final look that is deferred to a newer attempt is a hand-over, not a failed delete.
/// L3: a reuse that answered a definitive error before its start was sent is not kept under watch.
/// </summary>
public partial class LateCreateAfterCancelTests
{
    /// <summary>A gateway whose delete runs a hook instead of deleting (a delete that is accepted and does nothing yet).</summary>
    private sealed class HookedDeleteCompute(FakeGcp inner, Func<Task> onDelete) : IComputeGateway
    {
        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => onDelete();

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }

    private static async Task<VmSpec> SeedStoppedDonorAsync(FakeGcp gcp, Rig rig, string oldJob)
    {
        var oldSpec = Spec(oldJob);
        await gcp.CreateVmAsync(oldSpec, ZoneA, CancellationToken.None);
        await gcp.StopVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None);
        await rig.Repo.UpsertAsync(new RunRecord(oldJob, JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
        return oldSpec;
    }

    [Fact]
    public async Task R5_H1_a_reuse_relabelled_but_not_yet_started_is_watched_and_stopped_when_its_start_lands()
    {
        // Round 6 H1: the production shape. The real gateway honours the token, so the cancel ends the reuse task Canceled (IsCompleted) within milliseconds although Compute already accepted instances.start.
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(10); // only bounds a start that never lands: this one does
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-oldh1");
        await rig.SeedRowAsync("job-r5h1");
        var spec = Spec("job-r5h1");
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r5h1", spec), spec, new RunProgress(), cts.Token);
        await gcp.ReuseParkedAfterClaim.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        (await gcp.FindByJobIdAsync("job-r5h1", CancellationToken.None)).ShouldContain(v => v.Name == oldSpec.VmName, "the donor is relabelled to the new job and still stopped: the middle state");
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);

        await rig.Canceller.CancelAsync("job-r5h1", CancellationToken.None);
        await gcp.LandAcceptedReuseStartsAsync(); // the start POST lands after the cancel's last look
        await rig.Canceller.WhenReapsEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        rig.Outcomes.Reader.TryRead(out var outcome).ShouldBeTrue("the cancel released the relabelled donor unwatched (its task ended Canceled): nothing stopped the VM its start then ran");

        outcome!.State.ShouldBe(LateCreateState.ReuseStopped, "the cancel saw a stopped, relabelled donor and released it unwatched: its start then ran a GPU VM under a Cancelled row");
        (await gcp.GetVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty("the VM is another run's: stopped, never deleted");
    }

    [Fact]
    public async Task R5_L1_a_final_look_that_finds_a_newer_attempt_owning_the_VM_is_a_hand_over_not_a_failed_delete()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-r5l1");
        await gcp.CreateVmAsync(spec, ZoneA, CancellationToken.None);
        SentCreate? sent = null;
        Rig? rigRef = null;
        var calls = 0;
        var compute = new HookedDeleteCompute(
            gcp,
            async () =>
            {
                // The first delete is accepted and does nothing yet; meanwhile a newer attempt of the job starts (its delete span ended, so it may begin).
                if (Interlocked.Increment(ref calls) == 1)
                {
                    rigRef!.Terminator.Ledger.EndDelete(sent!);
                    // Round 6 L2: the newer attempt ADOPTS the VM (it owns the name now): only that is a hand-over. A newer attempt that is merely pending is waited for.
                    var newer = await rigRef.Terminator.Ledger.BeginAttemptAsync("job-r5l1", CancellationToken.None);
                    rigRef.Terminator.Ledger.NoteOwned("job-r5l1", sent!.Name, ZoneA, newer);
                }
            });
        var rig = new Rig(gcp, compute);
        rigRef = rig;
        rig.Settings.LifecycleTimeout = TimeSpan.Zero; // the watch takes its last look in the same round as its first delete
        var generation = await rig.Terminator.Ledger.BeginAttemptAsync("job-r5l1", CancellationToken.None);
        rig.Terminator.Ledger.EndAttempt("job-r5l1", generation);
        sent = new SentCreate("job-r5l1", spec.VmName, ZoneA, generation, spec.InstallationId) { Create = Task.CompletedTask };

        var outcomes = await rig.Terminator.ReapLateCreatesAsync([sent]);

        outcomes[0].State.ShouldBe(LateCreateState.Superseded, "a newer attempt may adopt this VM: nothing failed, so no late_create_unconfirmed");
    }

    [Theory]
    [InlineData("stockout")]
    [InlineData("gone")]
    public async Task R5_L3_a_reuse_that_answered_a_definitive_error_before_its_start_is_not_kept_for_a_later_cancel_to_watch(string how)
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-oldl3-" + how);
        switch (how)
        {
            case "stockout":
                gcp.WithReuseStartFailure(CloudErrorKind.Stockout);
                break;
            default:
                gcp.WithReuseBarrier(_ => gcp.DeleteVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None)); // VM_NOT_FOUND (404, kind Other) before anything was written
                break;
        }

        var jobId = "job-r5l3-" + how;
        await rig.SeedRowAsync(jobId);
        var spec = Spec(jobId);
        await rig.Provisioner.ProvisionAsync(TestInputs.Request(jobId, spec), spec, new RunProgress(), CancellationToken.None);

        var held = rig.Provisioner.ClaimSentCreates(jobId);

        held.ShouldNotContain(c => c.IsReuse, "the start was refused or never sent: nothing of the donor can appear, and a later cancel would watch it for the grace and hold the delete barrier");
    }

    [Fact]
    public async Task R5_L3_a_reuse_whose_answer_was_lost_stays_held_because_its_start_may_have_run()
    {
        var gcp = new FakeGcp().WithReuseResponseLost();
        var rig = new Rig(gcp);
        await SeedStoppedDonorAsync(gcp, rig, "job-oldl3n");
        await rig.SeedRowAsync("job-r5l3n");
        var spec = Spec("job-r5l3n");
        await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r5l3n", spec), spec, new RunProgress(), CancellationToken.None);

        rig.Provisioner.ClaimSentCreates("job-r5l3n").ShouldContain(c => c.IsReuse);
    }
}
