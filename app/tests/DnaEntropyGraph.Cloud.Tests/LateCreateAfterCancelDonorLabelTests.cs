using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #970: a reuse that fails or is cancelled leaves the donor VM STOPPED but carrying the NEW job's labels (job-id, lifecycle), which makes it visible to the new job's label lookups. The fix restores
/// the donor's ORIGINAL labels once the VM is confirmed stopped, never clobbers a newer owner, uses the run's own project and account, records what happened on the run's row, and a restore that fails
/// leaves the #938 by-name guards (a VM not named deg-job is never deleted) as the backstop.
/// </summary>
public partial class LateCreateAfterCancelTests
{
    private static void SameLabels(IReadOnlyDictionary<string, string>? actual, IReadOnlyDictionary<string, string>? expected, string because)
    {
        actual.ShouldNotBeNull(because);
        expected.ShouldNotBeNull();
        string Flat(IReadOnlyDictionary<string, string> labels) => string.Join(",", labels.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        Flat(actual).ShouldBe(Flat(expected), because);
    }

    private static async Task<IReadOnlyDictionary<string, string>> LabelsOfAsync(FakeGcp gcp, string vmName)
        => (await gcp.GetVmAsync(vmName, ZoneA, CancellationToken.None))!.Labels!;

    /// <summary>The reuse parks after its claim and the client cancels it: the production shape of "the cancel's settle wait gave up before the reuse answered".</summary>
    private static async Task<(string OldVm, IReadOnlyDictionary<string, string> DonorLabels)> CancelAParkedReuseAsync(FakeGcp gcp, Rig rig, string oldJob, string newJob)
    {
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, oldJob);
        var donorLabels = await LabelsOfAsync(gcp, oldSpec.VmName);
        await rig.SeedRowAsync(newJob);
        var spec = Spec(newJob);
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request(newJob, spec), spec, new RunProgress(), cts.Token);
        await gcp.ReuseParkedAfterClaim.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        (await LabelsOfAsync(gcp, oldSpec.VmName))["job-id"].ShouldBe(newJob, "the middle state: the donor is claimed for the new job and still stopped");
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);
        await rig.Canceller.CancelAsync(newJob, CancellationToken.None);
        return (oldSpec.VmName, donorLabels);
    }

    [Fact]
    public async Task D970_a_reuse_that_lands_after_the_cancel_gave_up_is_stopped_not_deleted_and_the_donors_original_labels_are_restored()
    {
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(10); // bounds only a start that never lands: this one does
        var (oldVm, donorLabels) = await CancelAParkedReuseAsync(gcp, rig, "job-old970a", "job-n970a");

        await gcp.LandAcceptedReuseStartsAsync(); // the start Compute had accepted runs now: the VM reads RUNNING under the Cancelled job's labels
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.Restored);
        var vm = (await gcp.GetVmAsync(oldVm, ZoneA, CancellationToken.None))!;
        vm.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty("#938/#999: a reused VM belongs to another run and is never deleted");
        SameLabels(vm.Labels, donorLabels, "the donor's labels (job-id, lifecycle) are back exactly as before the reuse");
        (await gcp.FindByJobIdAsync("job-n970a", CancellationToken.None)).ShouldBeEmpty("the Cancelled job's label no longer finds the donor");
        (await gcp.FindByJobIdAsync("job-old970a", CancellationToken.None)).ShouldContain(v => v.Name == oldVm, "the donor's own job finds it again, so its keep-alive sweep applies");
        (await gcp.ListByInstallationAsync("install-1", CancellationToken.None)).ShouldContain(v => v.Name == oldVm && v.Labels!["job-id"] == "job-old970a", "the reconciler's installation sweep sees the donor under its own job");
        var detail = rig.LastRow("job-n970a").ErrorDetail;
        detail.ShouldNotBeNull();
        detail.ShouldContain("stopped again");
        detail.ShouldContain("earlier labels were restored");
        detail.ShouldNotContain(oldVm, Case.Insensitive, "the row records states, never a VM name");
    }

    [Fact]
    public async Task D970_the_donors_labels_are_not_restored_before_the_start_has_answered_or_the_grace_has_run()
    {
        // A restore before the start landed would let the start run a VM that no label of the Cancelled job finds.
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(10);
        var (oldVm, donorLabels) = await CancelAParkedReuseAsync(gcp, rig, "job-old970b", "job-n970b");

        var reap = rig.Canceller.WhenReapsEndedAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken); // many watch rounds with the donor stopped, claimed and its task ended
        reap.IsCompleted.ShouldBeFalse();
        (await LabelsOfAsync(gcp, oldVm))["job-id"].ShouldBe("job-n970b", "until the start answered or the grace ran the labels stay on the job whose start may still land");
        gcp.RestoreAttempts.ShouldBe(0);

        await gcp.LandAcceptedReuseStartsAsync();
        await reap.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        SameLabels(await LabelsOfAsync(gcp, oldVm), donorLabels, "restored once the start was seen and the VM stopped");
    }

    [Fact]
    public async Task D970_a_reuse_that_never_lands_is_restored_after_the_grace()
    {
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        var (oldVm, donorLabels) = await CancelAParkedReuseAsync(gcp, rig, "job-old970c", "job-n970c");

        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.Restored);
        SameLabels(await LabelsOfAsync(gcp, oldVm), donorLabels, "the start never landed: the donor is stopped and back under its own labels");
    }

    [Fact]
    public async Task D970_a_restore_that_fails_is_recorded_with_its_class_leaves_the_VM_stopped_and_never_deletes_it()
    {
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels().WithRestoreFailure(CloudErrorKind.Permission);
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(10);
        var (oldVm, _) = await CancelAParkedReuseAsync(gcp, rig, "job-old970d", "job-n970d");

        await gcp.LandAcceptedReuseStartsAsync();
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        var restore = outcome.Restore.ShouldNotBeNull();
        restore.State.ShouldBe(DonorRestoreState.Failed);
        restore.ErrorClass.ShouldBe("permission");
        outcome.LogClass.ShouldContain("Failed:permission");
        var vm = (await gcp.GetVmAsync(oldVm, ZoneA, CancellationToken.None))!;
        vm.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        vm.Labels!["job-id"].ShouldBe("job-n970d", "the failed restore left the new job's labels: the by-name guards are the backstop");
        gcp.DeleteAttempts.ShouldBeEmpty();
        var detail = rig.LastRow("job-n970d").ErrorDetail;
        detail.ShouldNotBeNull();
        detail.ShouldContain("could not be restored (permission)");
        detail.ShouldContain("Cloud page");
        detail.ShouldNotContain("—", Case.Sensitive, "no em dashes in anything a user can see");
    }

    [Fact]
    public async Task D970_a_donor_a_newer_run_took_meanwhile_is_never_clobbered()
    {
        // Another run reused the donor after this run's claim was undone elsewhere: the VM now carries the newer run's job id, so the restore writes nothing.
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-old970e");
        var donorLabels = await LabelsOfAsync(gcp, oldSpec.VmName);
        var scope = TestScopedGateways.Default;
        rig.Terminator.NoteDonor(scope, "job-n970e", oldSpec.VmName, ZoneA, donorLabels);
        var newer = new Dictionary<string, string>(donorLabels) { ["job-id"] = "job-newer970e", ["lifecycle"] = "delete" };
        gcp.SimulateExternalRelabel(oldSpec.VmName, ZoneA, newer);

        var outcome = await rig.Terminator.RestoreDonorLabelsAsync("job-n970e", oldSpec.VmName, ZoneA);

        outcome.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.NewerOwner);
        SameLabels(await LabelsOfAsync(gcp, oldSpec.VmName), newer, "the newer run's labels are untouched");
    }

    [Fact]
    public async Task D970_a_donor_that_is_still_running_is_not_restored_until_it_is_confirmed_stopped()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        rig.Settings.LifecycleTimeout = TimeSpan.FromMilliseconds(50);
        var spec = Spec("job-old970f");
        await gcp.CreateVmAsync(spec, ZoneA, CancellationToken.None); // running, never stopped
        var labels = await LabelsOfAsync(gcp, spec.VmName);
        rig.Terminator.NoteDonor(TestScopedGateways.Default, "job-n970f", spec.VmName, ZoneA, labels);

        var outcome = await rig.Terminator.RestoreDonorLabelsAsync("job-n970f", spec.VmName, ZoneA);

        outcome.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.NotStopped);
        gcp.RestoreAttempts.ShouldBe(0, "labels move only once the VM is stopped");
    }

    [Fact]
    public async Task D970_no_donor_noted_means_nothing_is_restored()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);

        (await rig.Terminator.RestoreDonorLabelsAsync("job-none970", "deg-job-x", ZoneA)).ShouldBeNull();

        gcp.RestoreAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task D970_a_cancel_after_a_successful_reuse_stops_the_donor_restores_its_labels_in_the_runs_own_project_and_account_and_the_row_says_so()
    {
        var gcp = new FakeGcp().WithAccount("sub-run", "run@example.com", Project);
        var rig = new Rig(gcp);
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-old970g");
        var donorLabels = await LabelsOfAsync(gcp, oldSpec.VmName);
        await rig.SeedRowAsync("job-n970g");
        var spec = Spec("job-n970g");
        var request = TestInputs.Request("job-n970g", spec) with { Scope = new CloudScope(Project, "sub-run") };

        var provisioned = await rig.Provisioner.ProvisionAsync(request, spec, new RunProgress(), CancellationToken.None);

        provisioned.Success.ShouldBeTrue();
        (await gcp.GetVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBe("RUNNING");
        await rig.Canceller.CancelAsync("job-n970g", CancellationToken.None);

        var vm = (await gcp.GetVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None))!;
        vm.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty();
        SameLabels(vm.Labels, donorLabels, "the cancelled run's borrowed VM is back under its donor's labels");
        var call = gcp.CallsTo("RestoreVmLabels").ShouldHaveSingleItem();
        call.ProjectId.ShouldBe(Project);
        call.AccountSub.ShouldBe("sub-run", "the restore runs as the account the run was made under, not whatever is selected when the cancel happens");
        var row = rig.LastRow("job-n970g");
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.ErrorDetail.ShouldNotBeNull().ShouldContain("earlier labels were restored");
    }

    /// <summary>A failed run whose reuse start was accepted but whose answer was lost (a Network failure): the ladder fails the run through the failure funnel with the reuse still unanswered.</summary>
    private static async Task<(string OldVm, IReadOnlyDictionary<string, string> DonorLabels, ProvisionResult Result)> FailARunWhoseReuseAnswerWasLostAsync(FakeGcp gcp, Rig rig, string oldJob, string newJob)
    {
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, oldJob);
        var donorLabels = await LabelsOfAsync(gcp, oldSpec.VmName);
        await rig.SeedRowAsync(newJob);
        var spec = Spec(newJob);
        var result = await rig.Provisioner.ProvisionAsync(TestInputs.Request(newJob, spec), spec, new RunProgress(), CancellationToken.None);
        return (oldSpec.VmName, donorLabels, result);
    }

    [Fact]
    public async Task D970r2_a_failed_run_whose_reuse_start_lands_late_ends_stopped_under_the_donors_labels_never_running_under_them()
    {
        var gcp = new FakeGcp().WithReuseStartLostThatLandsLater();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(30); // bounds only a start that never lands: this one does
        var (oldVm, donorLabels, result) = await FailARunWhoseReuseAnswerWasLostAsync(gcp, rig, "job-old970d", "job-n970d");

        result.Success.ShouldBeFalse("the lost answer fails the run through the failure funnel");
        (await LabelsOfAsync(gcp, oldVm))["job-id"].ShouldBe("job-n970d", "the start has not answered, so the donor is NOT handed back yet: a late start would run it under the donor's labels and its keep-alive");

        await gcp.LandAcceptedReuseStartsAsync(); // the start Compute had accepted runs now
        var outcome = await rig.NextOutcomeAsync();
        await rig.Provisioner.WhenReuseWatchesEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.Restored);
        var vm = (await gcp.GetVmAsync(oldVm, ZoneA, CancellationToken.None))!;
        vm.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        SameLabels(vm.Labels, donorLabels, "stopped first, then handed back");
        gcp.DeleteAttempts.ShouldBeEmpty("#999: a borrowed VM is never deleted");
    }

    [Fact]
    public async Task D970r2_a_failed_run_whose_reuse_start_never_lands_gets_the_donor_back_after_the_grace()
    {
        var gcp = new FakeGcp().WithReuseStartLostThatLandsLater();
        var rig = new Rig(gcp); // the default grace is short
        var (oldVm, donorLabels, _) = await FailARunWhoseReuseAnswerWasLostAsync(gcp, rig, "job-old970e", "job-n970e");

        var outcome = await rig.NextOutcomeAsync();
        await rig.Provisioner.WhenReuseWatchesEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        SameLabels(await LabelsOfAsync(gcp, oldVm), donorLabels, "the start never landed: the donor is stopped and back under its own labels");
        rig.Provisioner.SentCount.ShouldBe(0, "the watch released what it held");
        rig.Terminator.DonorStateCount.ShouldBe(0);
    }

    [Fact]
    public void D970r2_a_donor_whose_job_never_released_its_claimed_creates_is_dropped_once_it_is_older_than_the_watch_can_be()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-31T08:00:00Z"), timestampFollowsClock: true);
        var rig = new Rig(new FakeGcp());
        rig.Settings.TimeProvider = clock;
        var labels = new Dictionary<string, string> { ["job-id"] = "job-donor" };
        var scope = new CloudScope("my-project");

        rig.Terminator.NoteDonor(scope, "job-leaked", "deg-job-donor", ZoneA, labels);
        rig.Terminator.NoteDonor(scope, "job-young", "deg-job-donor", ZoneA, labels);
        rig.Terminator.DonorCount.ShouldBe(2, "nothing is old yet");

        clock.Advance(TimeSpan.FromHours(2)); // far beyond the reap window, the grace and the lifecycle waits
        rig.Terminator.NoteDonor(scope, "job-new", "deg-job-donor", ZoneA, labels);

        rig.Terminator.DonorCount.ShouldBe(1, "both older donors are dropped when the next one is noted; only the new one remains");
    }

    [Fact]
    public async Task D970r2_a_stand_down_that_sees_the_Cancelled_row_does_not_take_the_restore_sentence_the_cancel_has_yet_to_write()
    {
        // The cancel records Cancelled and THEN annotates the row with what became of the donor's labels. A run that stood down in between reads a terminal Cancelled row: it must not throw the outcome away.
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo);
        var spec = Spec("job-n970f");
        var oldSpec = await SeedStoppedDonorRawAsync(gcp, "job-old970f");
        var scope = new CloudScope("my-project");
        runner.TerminatorForTests.NoteDonor(scope, "job-n970f", oldSpec.VmName, ZoneA, await LabelsOfAsync(gcp, oldSpec.VmName));
        await runner.TerminatorForTests.RestoreAndNoteAsync("job-n970f", oldSpec.VmName, ZoneA); // the donor still carries its own job-id: a newer owner, an outcome to note
        await repo.UpsertAsync(new RunRecord("job-n970f", JobPhase.Cancelled, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await runner.RunAsync(TestInputs.Request("job-n970f", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Cancelled);
        runner.TerminatorForTests.TakeDonorRestoreSentence("job-n970f").ShouldNotBeNull("the cancel's annotation still has its sentence to write");
    }

    private static async Task<VmSpec> SeedStoppedDonorRawAsync(FakeGcp gcp, string oldJob)
    {
        var oldSpec = Spec(oldJob);
        await gcp.CreateVmAsync(oldSpec, ZoneA, CancellationToken.None);
        await gcp.StopVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None);
        return oldSpec;
    }

    [Fact]
    public void D970_the_row_sentence_names_an_action_for_every_outcome_that_needs_one_and_has_no_em_dash()
    {
        foreach (var state in Enum.GetValues<DonorRestoreState>())
        {
            var sentence = DonorRestoreOutcome.SentenceFor([new DonorRestoreOutcome("job-x", state, state == DonorRestoreState.Failed ? "permission" : null)]);
            if (state == DonorRestoreState.Gone)
            {
                sentence.ShouldBeNull("a VM that is gone needs no sentence");
                continue;
            }

            sentence.ShouldNotBeNull();
            sentence.ShouldNotContain("—");
            if (state is DonorRestoreState.NotStopped or DonorRestoreState.Failed)
            {
                sentence.ShouldContain("Cloud page", Case.Sensitive, "Hard Rule 13: every error names one action");
            }
        }
    }
}
