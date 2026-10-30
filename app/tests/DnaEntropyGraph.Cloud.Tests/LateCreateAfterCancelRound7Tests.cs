using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #938, round 7 (and #999). A donor a failed reuse left relabelled for the job is another run's VM: the extras sweeps stop it (never delete it), a resume never adopts it while it is stopped,
/// a borrowed VM that is still booting is stopped once it runs, and a read-back that could only not look says so.
/// </summary>
public partial class LateCreateAfterCancelTests
{
    private const string ZoneC = "us-central1-c";

    /// <summary>A stopped donor of another run is claimed for <paramref name="jobId"/> by a reuse whose start then fails (stockout), and the ladder wins in zone B.</summary>
    private static async Task<(Rig Rig, FakeGcp Gcp, VmSpec Donor, CloudJobRequest Request)> FailedReuseThenLadderWinsInZoneBAsync(string jobId)
    {
        var gcp = new FakeGcp().WithReuseStartFailure(CloudErrorKind.Stockout);
        var rig = new Rig(gcp);
        var donor = await SeedStoppedDonorAsync(gcp, rig, "job-old-" + jobId);
        await rig.SeedRowAsync(jobId);
        var spec = Spec(jobId);
        var request = TestInputs.Request(jobId, spec, zones: [ZoneB]);
        var result = await rig.Provisioner.ProvisionAsync(request, spec, new RunProgress(), CancellationToken.None);
        result.Success.ShouldBeTrue("the reuse failed with a stockout and the ladder created a VM in zone B");
        result.Zone.ShouldBe(ZoneB);
        (await gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldContain(v => v.Name == donor.VmName, "the failed reuse left the donor relabelled for this job");
        return (rig, gcp, donor, request);
    }

    [Fact]
    public async Task R7_M1_the_other_zones_sweep_stops_the_donor_a_failed_reuse_left_and_never_deletes_it()
    {
        var (rig, gcp, donor, request) = await FailedReuseThenLadderWinsInZoneBAsync("job-r7m1a");

        var note = await rig.Provisioner.DeleteOtherZonesAsync(request, ZoneB, CancellationToken.None);

        note.ShouldBeNull();
        gcp.DeleteAttempts.ShouldNotContain(d => d.Name == donor.VmName, "the donor is the earlier run's VM and disk");
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
    }

    [Fact]
    public async Task R7_M1_a_resume_that_finds_the_donor_and_the_new_VM_keeps_one_and_never_deletes_the_donor()
    {
        var (rig, gcp, donor, request) = await FailedReuseThenLadderWinsInZoneBAsync("job-r7m1b");
        var owned = await gcp.FindByJobIdAsync("job-r7m1b", CancellationToken.None);

        var (kept, note) = await rig.Provisioner.KeepOneOwnedVmAsync(request, owned, CancellationToken.None);

        note.ShouldBeNull();
        kept.Name.ShouldBe("deg-job-r7m1b");
        gcp.DeleteAttempts.ShouldNotContain(d => d.Name == donor.VmName);
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
    }

    [Fact]
    public async Task R7_M2_a_resume_whose_only_labelled_VM_is_a_stopped_donor_adopts_it_and_creates_no_new_VM_billed_without_a_startup_script()
    {
        // Round 8 H1+M1: round 7 skipped the stopped donor and provisioned a NEW VM (no startup script, stale status.json, a silently replaced lost VM). The donor is adopted: the existing
        // judgment (the wait on an already-stopped VM is Lost at once and reads result.json and status.json) then fails the run, the donor ends stopped, and no new VM is billed.
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        var donor = await SeedStoppedDonorAsync(gcp, rig, "job-oldr7m2");
        var spec = Spec("job-r7m2");
        gcp.SimulateExternalRelabel(donor.VmName, ZoneA, spec.ToLabels()); // what a failed reuse leaves behind
        await rig.SeedRowAsync("job-r7m2");
        var progress = new RunProgress();

        var result = await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r7m2", spec), spec, progress, CancellationToken.None);

        result.Success.ShouldBeTrue();
        progress.VmName.ShouldBe(donor.VmName);
        progress.VmAdopted.ShouldBeTrue("the stopped donor is adopted and judged by the existing path, never replaced");
        (await gcp.GetVmAsync("deg-job-r7m2", ZoneA, CancellationToken.None)).ShouldBeNull("no new VM is created for a resume that found the donor");
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty();
    }
    [Fact]
    public async Task R7_M3_a_borrowed_VM_that_is_still_booting_is_stopped_once_it_runs_never_deleted()
    {
        var gcp = new FakeGcp().WithBootStatusPolls(5);
        var compute = new ScriptedCompute(gcp, rejectStopWhileBooting: true);
        var rig = new Rig(gcp, compute);
        var donor = Spec("job-oldr7m3");
        await gcp.CreateVmAsync(donor, ZoneA, CancellationToken.None);
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("PROVISIONING", "STAGING");

        var error = await rig.Terminator.EnsureVmEndedAsync(TestInputs.Request("job-r7m3", Spec("job-r7m3")), ZoneA, AfterTaskAction.Delete, CancellationToken.None, donor.VmName, ownOnly: true);

        error.ShouldBeNull();
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty("a borrowed VM is never deleted, even while it boots");
        compute.RejectedStops.ShouldBe(0, "no stop is sent to a VM that is still booting: it may be refused");
    }

    [Fact]
    public async Task R7_L1_a_read_back_that_could_only_not_look_says_it_could_not_check_never_that_a_VM_is_still_there()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-r7l1");
        await gcp.CreateVmAsync(spec, ZoneA, CancellationToken.None);
        var compute = new ScriptedCompute(gcp, failFind: n => n >= 2); // the first listing finds the VM, every later one fails
        var rig = new Rig(gcp, compute);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(200);
        var generation = await rig.Terminator.Ledger.BeginAttemptAsync("job-r7l1", CancellationToken.None);
        rig.Terminator.Ledger.EndAttempt("job-r7l1", generation);
        var sent = new SentCreate("job-r7l1", spec.VmName, ZoneA, generation, spec.InstallationId) { Create = Task.CompletedTask };

        var result = await rig.Terminator.DeleteOwnedVmsAndVerifyAsync("job-r7l1", CreateSettleState.Settled, CancellationToken.None, [sent]);

        result.ShouldNotBeNull();
        result.ShouldContain("could not check", Case.Insensitive);
        result.ShouldContain("Cloud page");
        result.ShouldNotContain("still there");
    }

    [Fact]
    public async Task R7_L2_a_real_cancel_after_a_reuse_that_failed_with_an_error_from_after_the_start_ends_the_VM_stopped()
    {
        var gcp = new FakeGcp();
        var stopsRefused = true;
        var error = new CloudOperationException(new CloudError("PERMISSION_DENIED", 403, "denied"), CloudErrorKind.Permission);
        var compute = new ScriptedCompute(gcp, afterReuse: () => Task.FromException(error), rejectStop: () => stopsRefused);
        var rig = new Rig(gcp, compute);
        var donor = await SeedStoppedDonorAsync(gcp, rig, "job-oldr7l2");
        await rig.SeedRowAsync("job-r7l2");
        var spec = Spec("job-r7l2");
        await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r7l2", spec), spec, new RunProgress(), CancellationToken.None);
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBe("RUNNING", "the start ran and the failure path's stop was refused");

        stopsRefused = false;
        await rig.Canceller.CancelAsync("job-r7l2", CancellationToken.None);

        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty();
    }

    [Fact]
    public void R7_L3_the_error_class_of_a_cloud_error_is_the_run_error_code_roster_name()
    {
        VmTerminator.ErrorClassOf(new CloudOperationException(new CloudError("X", 403, "x"), CloudErrorKind.ApiDisabled)).ShouldBe("api_disabled");
        VmTerminator.ErrorClassOf(new CloudOperationException(new CloudError("X", 412, "x"), CloudErrorKind.OrgPolicy)).ShouldBe("org_policy");
        VmTerminator.ErrorClassOf(new InvalidOperationException()).ShouldBe("other");
    }
}
