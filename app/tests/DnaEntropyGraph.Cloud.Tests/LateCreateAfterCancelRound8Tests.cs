using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #938, round 8 (and #999). M2: a resume that finds both the donor and its own VM keeps its own. L1/L2/L3: the read-back and the borrowed-VM stop sentences say what actually happened.
/// </summary>
public partial class LateCreateAfterCancelTests
{
    [Fact]
    public async Task R8_M2_with_both_VMs_stopped_and_the_donor_listed_first_the_own_VM_is_kept_and_the_donor_is_never_deleted()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        var donor = await SeedStoppedDonorAsync(gcp, rig, "job-oldr8m2");
        var spec = Spec("job-r8m2");
        gcp.SimulateExternalRelabel(donor.VmName, ZoneA, spec.ToLabels());
        await gcp.CreateVmAsync(spec, ZoneB, CancellationToken.None);
        await gcp.StopVmAsync(spec.VmName, ZoneB, CancellationToken.None);
        var owned = (await gcp.FindByJobIdAsync("job-r8m2", CancellationToken.None)).OrderBy(v => v.Name == donor.VmName ? 0 : 1).ToList();
        owned[0].Name.ShouldBe(donor.VmName, "the donor is listed first and in the first zone");
        owned.Count.ShouldBe(2);

        var (kept, note) = await rig.Provisioner.KeepOneOwnedVmAsync(TestInputs.Request("job-r8m2", spec, zones: [ZoneA, ZoneB]), owned, CancellationToken.None);

        note.ShouldBeNull();
        kept.Name.ShouldBe("deg-job-r8m2");
        gcp.DeleteAttempts.ShouldNotContain(d => d.Name == donor.VmName);
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task R8_L1_a_VM_seen_once_whose_later_looks_read_gone_or_fail_says_it_could_not_check_never_still_there()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-r8l1");
        await gcp.CreateVmAsync(spec, ZoneA, CancellationToken.None);
        var compute = new ScriptedCompute(gcp, failFind: n => n >= 3, lateDelete: true); // listing 1 shows the VM, then it is gone; every later listing fails
        var rig = new Rig(gcp, compute);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(200);
        var generation = await rig.Terminator.Ledger.BeginAttemptAsync("job-r8l1", CancellationToken.None);
        rig.Terminator.Ledger.EndAttempt("job-r8l1", generation);
        var sent = new SentCreate("job-r8l1", spec.VmName, ZoneA, generation, spec.InstallationId) { Create = Task.CompletedTask };

        var result = await rig.Terminator.DeleteOwnedVmsAndVerifyAsync("job-r8l1", CreateSettleState.Settled, CancellationToken.None, [sent]);

        result.ShouldNotBeNull();
        result.ShouldContain("could not check", Case.Insensitive);
        result.ShouldContain("Cloud page");
        result.ShouldNotContain("still there");
    }

    [Fact]
    public async Task R8_L2_a_borrowed_VM_stuck_booting_past_the_timeout_says_the_stop_could_not_be_sent_while_it_was_starting()
    {
        var gcp = new FakeGcp().WithBootStatusPolls(100000);
        var compute = new ScriptedCompute(gcp, rejectStopWhileBooting: true);
        var rig = new Rig(gcp, compute);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(200);
        var donor = Spec("job-oldr8l2");
        await gcp.CreateVmAsync(donor, ZoneA, CancellationToken.None);

        var error = await rig.Terminator.EnsureVmEndedAsync(TestInputs.Request("job-r8l2", Spec("job-r8l2")), ZoneA, AfterTaskAction.Delete, CancellationToken.None, donor.VmName, ownOnly: true);

        error.ShouldNotBeNull();
        error.ShouldNotContain("after Stop", Case.Insensitive, "no stop was ever sent");
        error.ShouldContain("could not be sent", Case.Insensitive);
        error.ShouldContain("Cloud page");
        gcp.DeleteAttempts.ShouldBeEmpty();
        compute.RejectedStops.ShouldBe(0);
    }

    [Fact]
    public async Task R8_L3_a_stop_refused_once_the_booting_borrowed_VM_runs_is_reported_not_confirmed_stopped_and_the_VM_is_never_deleted()
    {
        var gcp = new FakeGcp().WithBootStatusPolls(2);
        var compute = new ScriptedCompute(gcp, rejectStop: () => true, rejectStopWhileBooting: true);
        var rig = new Rig(gcp, compute);
        var donor = Spec("job-oldr8l3");
        await gcp.CreateVmAsync(donor, ZoneA, CancellationToken.None);

        var error = await rig.Terminator.EnsureVmEndedAsync(TestInputs.Request("job-r8l3", Spec("job-r8l3")), ZoneA, AfterTaskAction.Delete, CancellationToken.None, donor.VmName, ownOnly: true);

        error.ShouldNotBeNull();
        error.ShouldContain("could not be confirmed stopped");
        gcp.DeleteAttempts.ShouldBeEmpty();
        (await gcp.GetVmAsync(donor.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBe("RUNNING");
    }
}
