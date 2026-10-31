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
            if (state is DonorRestoreState.NotStopped or DonorRestoreState.Failed or DonorRestoreState.NotRestored)
            {
                sentence.ShouldContain("Cloud page", Case.Sensitive, "Hard Rule 13: every error names one action");
            }
        }
    }

    // ---- round 3 ----

    private static SentCreate UnansweredReuse(string jobId, string vmName) => new(TestScopedGateways.Default, jobId, vmName, ZoneA, 1, "install-1", isReuse: true);

    [Fact]
    public void D970r3_ending_the_watch_of_one_VM_keeps_the_donor_and_the_unanswered_reuse_of_another_VM_of_the_same_job()
    {
        var rig = new Rig(new FakeGcp());
        var labels = new Dictionary<string, string> { ["job-id"] = "job-donor" };
        var scope = TestScopedGateways.Default;
        rig.Terminator.NoteDonor(scope, "job-r3", "deg-job-vma", ZoneA, labels);
        rig.Terminator.NoteDonor(scope, "job-r3", "deg-job-vmb", ZoneA, labels);
        var vmaSend = UnansweredReuse("job-r3", "deg-job-vma");
        rig.Terminator.NoteReuseSend(vmaSend);
        rig.Terminator.NoteReuseSend(UnansweredReuse("job-r3", "deg-job-vmb"));

        rig.Terminator.ForgetDonor(vmaSend);

        rig.Terminator.DonorCount.ShouldBe(1, "only the VM whose watch ended is forgotten");
        rig.Terminator.ReuseUnanswered("job-r3", "deg-job-vma", ZoneA).ShouldBeFalse();
        rig.Terminator.ReuseUnanswered("job-r3", "deg-job-vmb", ZoneA).ShouldBeTrue("a later attempt's reuse of another VM is still unanswered: its donor must not be restored under it");
    }

    [Fact]
    public async Task D970r3_the_end_of_a_failed_reuses_watch_does_not_drop_another_VMs_donor_of_the_same_job()
    {
        var gcp = new FakeGcp().WithReuseStartLostThatLandsLater();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(30);
        _ = await FailARunWhoseReuseAnswerWasLostAsync(gcp, rig, "job-old970g3", "job-n970g3");
        rig.Terminator.NoteDonor(TestScopedGateways.Default, "job-n970g3", "deg-job-other", ZoneA, new Dictionary<string, string> { ["job-id"] = "job-donor" });
        rig.Terminator.NoteReuseSend(UnansweredReuse("job-n970g3", "deg-job-other"));

        await gcp.LandAcceptedReuseStartsAsync();
        await rig.Provisioner.WhenReuseWatchesEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        rig.Terminator.DonorCount.ShouldBe(1, "the other VM's donor outlives this VM's watch");
        rig.Terminator.ReuseUnanswered("job-n970g3", "deg-job-other", ZoneA).ShouldBeTrue();
    }

    [Fact]
    public async Task D970r3_the_end_of_a_cancels_watch_does_not_drop_another_VMs_donor_of_the_same_job()
    {
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(30);
        _ = await CancelAParkedReuseAsync(gcp, rig, "job-old970h3", "job-n970h3");
        rig.Terminator.NoteDonor(TestScopedGateways.Default, "job-n970h3", "deg-job-other", ZoneA, new Dictionary<string, string> { ["job-id"] = "job-donor" });
        rig.Terminator.NoteReuseSend(UnansweredReuse("job-n970h3", "deg-job-other"));

        await gcp.LandAcceptedReuseStartsAsync();
        await rig.Canceller.WhenReapsEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        rig.Terminator.DonorCount.ShouldBe(1, "the other VM's donor outlives this cancel's watch");
        rig.Terminator.ReuseUnanswered("job-n970h3", "deg-job-other", ZoneA).ShouldBeTrue();
    }

    [Fact]
    public async Task D970r4_a_hung_reuse_start_that_outlasts_the_reap_window_writes_no_label_and_logs_NotRestored_StartUnanswered()
    {
        // Round 4 finding 2 (replaces the round 3 test that expected a restore): the start POST may still land after the CreateTimeout cut the call off, so no label is written while it may be on the wire.
        // The VM stays stopped under this job's labels; the #938 by-name guards are the backstop (#1052 tracks restart-safe recovery).
        var gcp = new FakeGcp().WithReuseParkedAfterClaim();
        var rig = new Rig(gcp);
        var logged = new List<DonorRestoreOutcome>();
        rig.Settings.OnDonorRestore = logged.Add;
        rig.Settings.CreateTimeout = TimeSpan.FromMilliseconds(100);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(400);
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-old970i");
        await rig.SeedRowAsync("job-n970i");
        var spec = Spec("job-n970i");

        _ = await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-n970i", spec), spec, new RunProgress(), CancellationToken.None);
        var outcome = await rig.NextOutcomeAsync();
        await rig.Provisioner.WhenReuseWatchesEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        outcome.State.ShouldBe(LateCreateState.GaveUp);
        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.NotRestored);
        outcome.Restore.ErrorClass.ShouldBe("StartUnanswered");
        logged.ShouldContain(o => o.LogClass == "NotRestored:StartUnanswered", "the donor-restore log line carries the state and the class only");
        gcp.RestoreAttempts.ShouldBe(0, "no label write while a start may be on the wire");
        (await LabelsOfAsync(gcp, oldSpec.VmName))["job-id"].ShouldBe("job-n970i", "left stopped under this job's labels");
        rig.Terminator.DonorCount.ShouldBe(0, "the watch's end forgets the donor");
        gcp.ReleaseParkedReuse();
    }

    [Fact]
    public async Task D970r4_a_watch_that_defers_to_a_newer_attempt_and_gives_up_writes_no_label_and_says_NotRestored()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(50);
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-old970j");
        var labels = await LabelsOfAsync(gcp, oldSpec.VmName);
        var scope = TestScopedGateways.Default;
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970j", CancellationToken.None); // generation 1: the sent reuse's
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970j", CancellationToken.None); // generation 2: a newer attempt is pending, so the watch defers
        rig.Terminator.NoteDonor(scope, "job-n970j", oldSpec.VmName, ZoneA, labels);
        var sent = new SentCreate(scope, "job-n970j", oldSpec.VmName, ZoneA, 1, "install-1", isReuse: true);
        rig.Terminator.NoteReuseSend(sent);

        var outcomes = await rig.Terminator.ReapLateCreatesAsync([sent]);

        var outcome = outcomes.ShouldHaveSingleItem();
        outcome.State.ShouldBe(LateCreateState.GaveUp);
        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.NotRestored);
        gcp.RestoreAttempts.ShouldBe(0);
    }

    [Fact]
    public void D970r4_a_watch_that_ends_forgets_only_its_own_send_never_a_later_attempts_of_the_same_VM()
    {
        // Round 4 finding 3. A reuse whose claim never wrote leaves the donor's labels on the VM, so a later attempt of the SAME job can pick the same VM (VmProvisioner's candidate filter excludes only a VM
        // already labelled with this job): the first attempt's watch must not drop the second attempt's donor and unanswered-reuse record, which share the (job, name, zone) key.
        var rig = new Rig(new FakeGcp());
        var scope = TestScopedGateways.Default;
        var labels = new Dictionary<string, string> { ["job-id"] = "job-donor" };
        var first = UnansweredReuse("job-r4", "deg-job-vma");
        var second = UnansweredReuse("job-r4", "deg-job-vma");
        rig.Terminator.NoteDonor(scope, "job-r4", "deg-job-vma", ZoneA, labels);
        rig.Terminator.NoteReuseSend(first);
        rig.Terminator.NoteDonor(scope, "job-r4", "deg-job-vma", ZoneA, labels);
        rig.Terminator.NoteReuseSend(second);

        rig.Terminator.ForgetDonor(first);

        rig.Terminator.DonorCount.ShouldBe(1, "the second attempt's donor survives the first watch's end");
        rig.Terminator.ReuseUnanswered("job-r4", "deg-job-vma", ZoneA).ShouldBeTrue();

        rig.Terminator.ForgetDonor(second);
        rig.Terminator.DonorCount.ShouldBe(0);
        rig.Terminator.ReuseUnanswered("job-r4", "deg-job-vma", ZoneA).ShouldBeFalse();
    }

    [Fact]
    public void D970r4_ending_a_job_without_a_watch_prunes_the_donors_of_other_jobs_that_expired()
    {
        // Round 4 finding 5: pruning ran only inside NoteDonor, so a leaked donor lived until the next reuse of any job.
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-31T08:00:00Z"), timestampFollowsClock: true);
        var rig = new Rig(new FakeGcp());
        rig.Settings.TimeProvider = clock;
        rig.Terminator.NoteDonor(new CloudScope("my-project"), "job-leaked", "deg-job-donor", ZoneA, new Dictionary<string, string> { ["job-id"] = "job-donor" });

        clock.Advance(TimeSpan.FromHours(2));
        rig.Provisioner.ForgetPendingRequests("job-unrelated");

        rig.Terminator.DonorCount.ShouldBe(0);
    }

    [Fact]
    public async Task D970r4_the_end_of_a_watch_prunes_donors_that_expired()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-31T08:00:00Z"), timestampFollowsClock: true);
        var gcp = new FakeGcp().WithReuseStartLostThatLandsLater();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(30);
        _ = await FailARunWhoseReuseAnswerWasLostAsync(gcp, rig, "job-old970k", "job-n970k");
        rig.Settings.TimeProvider = clock;
        rig.Terminator.NoteDonor(new CloudScope("my-project"), "job-leaked", "deg-job-donor", ZoneA, new Dictionary<string, string> { ["job-id"] = "job-donor" });
        clock.Advance(TimeSpan.FromHours(2));

        await gcp.LandAcceptedReuseStartsAsync();
        await rig.Provisioner.WhenReuseWatchesEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        rig.Terminator.DonorCount.ShouldBe(0, "the leaked donor expired and the watch's end pruned it");
    }

    // ---- round 5. The rule: never write labels while OUR start call is still pending; a call that has completed (answered, refused, Canceled, lost) and a VM stopped through the grace is restored. ----

    [Fact]
    public async Task D970r5_a_start_that_ended_Canceled_unanswered_on_a_VM_stopped_through_the_grace_is_restored_exactly_once()
    {
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        var (oldVm, donorLabels) = await CancelAParkedReuseAsync(gcp, rig, "job-old970m", "job-n970m");

        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.Restored);
        gcp.RestoreAttempts.ShouldBe(1);
        SameLabels(await LabelsOfAsync(gcp, oldVm), donorLabels, "the call completed (Canceled) and the VM read stopped through the grace");
    }

    [Fact]
    public async Task D970r5_the_same_start_landing_running_is_stopped_first_and_then_restored_exactly_once()
    {
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(10);
        var (oldVm, donorLabels) = await CancelAParkedReuseAsync(gcp, rig, "job-old970n", "job-n970n");

        await gcp.LandAcceptedReuseStartsAsync();
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        gcp.RestoreAttempts.ShouldBe(1);
        (await gcp.GetVmAsync(oldVm, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        SameLabels(await LabelsOfAsync(gcp, oldVm), donorLabels, "stopped by the watch, then restored");
    }

    private static async Task<(SentCreate Sent, string OldVm, IReadOnlyDictionary<string, string> DonorLabels)> NoteAReuseSendAsync(FakeGcp gcp, Rig rig, string oldJob, string newJob, bool claimLabels)
    {
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, oldJob);
        var donorLabels = await LabelsOfAsync(gcp, oldSpec.VmName);
        if (claimLabels)
        {
            _ = await gcp.ReuseVmAsync(Spec(newJob), oldSpec.VmName, ZoneA, CancellationToken.None); // the claim, as a reuse writes it
            await gcp.StopVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None);
        }

        var scope = TestScopedGateways.Default;
        rig.Terminator.NoteDonor(scope, newJob, oldSpec.VmName, ZoneA, donorLabels);
        var sent = rig.Provisioner.NoteSent(scope, newJob, oldSpec.VmName, ZoneA, "install-1", 1, reuse: true);
        rig.Terminator.NoteReuseSend(sent);
        return (sent, oldSpec.VmName, donorLabels);
    }

    [Fact]
    public async Task D970r6_a_watch_that_defers_to_a_second_attempt_that_never_reached_the_VM_restores_the_donor_when_the_window_is_over()
    {
        // Replaces D970r5_a_watch_that_defers...: Defer only says SOME newer attempt of the job is pending, not that it holds this VM. The instance says who owns it.
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(50);
        var (sent, oldVm, donorLabels) = await NoteAReuseSendAsync(gcp, rig, "job-old970o", "job-n970o", claimLabels: true);
        sent.Create = Task.CompletedTask; // answered
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970o", CancellationToken.None);
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970o", CancellationToken.None); // a newer attempt is pending: the watch defers

        var outcome = (await rig.Terminator.ReapLateCreatesAsync([sent])).ShouldHaveSingleItem();

        outcome.State.ShouldBe(LateCreateState.GaveUp);
        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.Restored);
        gcp.RestoreAttempts.ShouldBe(1);
        SameLabels(await LabelsOfAsync(gcp, oldVm), donorLabels, "the VM carried this job's labels and read stopped: the attempt that never claimed it cannot be using it");
    }

    [Fact]
    public async Task D970r6_a_watch_that_defers_while_a_second_attempt_recorded_its_own_send_for_the_same_VM_writes_nothing()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(50);
        var (sent, oldVm, _) = await NoteAReuseSendAsync(gcp, rig, "job-old970r", "job-n970r", claimLabels: true);
        sent.Create = Task.CompletedTask;
        rig.Terminator.NoteDonor(TestScopedGateways.Default, "job-n970r", oldVm, ZoneA, new Dictionary<string, string> { ["job-id"] = "job-old970r" });
        rig.Terminator.NoteReuseSend(UnansweredReuse("job-n970r", oldVm)); // the second attempt claimed this same VM
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970r", CancellationToken.None);
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970r", CancellationToken.None);

        var outcome = (await rig.Terminator.ReapLateCreatesAsync([sent])).ShouldHaveSingleItem();

        outcome.State.ShouldBe(LateCreateState.GaveUp);
        gcp.RestoreAttempts.ShouldBe(0, "the send-match makes this watch stand down");
        rig.Terminator.DonorCount.ShouldBe(1, "the second attempt's donor is untouched");
    }

    [Fact]
    public async Task D970r6_a_watch_that_defers_and_finds_the_VM_not_carrying_this_jobs_labels_logs_NotRestored_DeferredNewerAttempt()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        var logged = new List<DonorRestoreOutcome>();
        rig.Settings.OnDonorRestore = logged.Add;
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(50);
        var (sent, _, _) = await NoteAReuseSendAsync(gcp, rig, "job-old970s", "job-n970s", claimLabels: false); // the VM still carries the donor's labels
        sent.Create = Task.CompletedTask;
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970s", CancellationToken.None);
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970s", CancellationToken.None);

        var outcome = (await rig.Terminator.ReapLateCreatesAsync([sent])).ShouldHaveSingleItem();

        outcome.Restore.ShouldNotBeNull().State.ShouldBe(DonorRestoreState.NotRestored);
        outcome.Restore.ErrorClass.ShouldBe("DeferredNewerAttempt");
        logged.ShouldContain(o => o.LogClass == "NotRestored:DeferredNewerAttempt");
        gcp.RestoreAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task D970r6_a_stale_watch_does_not_consume_or_write_for_a_later_attempts_donor()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        var (first, oldVm, _) = await NoteAReuseSendAsync(gcp, rig, "job-old970t", "job-n970t", claimLabels: true); // stopped under job-n970t's labels
        first.Create = Task.CompletedTask; // answered: the watch would restore at once
        rig.Terminator.NoteDonor(TestScopedGateways.Default, "job-n970t", oldVm, ZoneA, new Dictionary<string, string> { ["job-id"] = "job-old970t" });
        rig.Terminator.NoteReuseSend(UnansweredReuse("job-n970t", oldVm)); // attempt 2 overwrote donor and send; its start is pending

        var outcome = (await rig.Terminator.ReapLateCreatesAsync([first])).ShouldHaveSingleItem();

        outcome.Restore.ShouldBeNull("another attempt's send owns the key");
        gcp.RestoreAttempts.ShouldBe(0, "no write while attempt 2's start is pending");
        rig.Terminator.DonorCount.ShouldBe(1, "attempt 2's donor was not consumed");
    }

    [Fact]
    public async Task D970r6_a_reuse_refused_before_anything_was_sent_leaves_no_send_record_behind()
    {
        var gcp = new FakeGcp().WithReuseStartFailure(CloudErrorKind.Stockout);
        var rig = new Rig(gcp);
        _ = await SeedStoppedDonorAsync(gcp, rig, "job-old970u");
        await rig.SeedRowAsync("job-n970u");
        var spec = Spec("job-n970u");

        _ = await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-n970u", spec), spec, new RunProgress(), CancellationToken.None);

        rig.Terminator.ReuseSendCount.ShouldBe(0, "the refused send's record is forgotten with its donor");
        rig.Terminator.DonorCount.ShouldBe(0);
    }

    [Fact]
    public async Task D970r5_a_start_that_is_not_the_recorded_send_of_the_key_is_not_reported_StartUnanswered()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateReapWindow = TimeSpan.FromMilliseconds(50);
        var (first, vm, _) = await NoteAReuseSendAsync(gcp, rig, "job-old970p", "job-n970p", claimLabels: false);
        rig.Terminator.NoteReuseSend(UnansweredReuse("job-n970p", vm)); // a later attempt's send now owns the key
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970p", CancellationToken.None);
        _ = await rig.Terminator.Ledger.BeginAttemptAsync("job-n970p", CancellationToken.None);

        var outcome = (await rig.Terminator.ReapLateCreatesAsync([first])).ShouldHaveSingleItem();

        outcome.Restore.ShouldBeNull("the donor and send under the key are the later attempt's, not this send's");
    }

    [Fact]
    public async Task D970r5_a_cancel_of_a_reuse_seen_landed_but_never_answered_restores_the_donor_instead_of_forgetting_it()
    {
        var gcp = new FakeGcp();
        var rig = new Rig(gcp);
        var (sent, oldVm, donorLabels) = await NoteAReuseSendAsync(gcp, rig, "job-old970q", "job-n970q", claimLabels: true);
        sent.Landed = true; // a look saw it; the start task never answered
        await rig.SeedRowAsync("job-n970q");

        await rig.Canceller.CancelAsync("job-n970q", CancellationToken.None);
        await rig.Canceller.WhenReapsEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        gcp.RestoreAttempts.ShouldBe(1, "the cancel's own restore skipped the unanswered start; the watch must do it, not drop the donor");
        SameLabels(await LabelsOfAsync(gcp, oldVm), donorLabels, "back under the donor's labels");
        rig.Terminator.DonorCount.ShouldBe(0);
    }

    [Fact]
    public void D970r5_a_forget_for_a_send_that_was_never_recorded_leaves_a_donor_noted_in_the_gap_before_its_send()
    {
        // NoteDonor runs before NoteReuseSend: a later attempt's donor with no send yet must survive an older watch's end.
        var rig = new Rig(new FakeGcp());
        rig.Terminator.NoteDonor(TestScopedGateways.Default, "job-r5", "deg-job-vma", ZoneA, new Dictionary<string, string> { ["job-id"] = "job-donor" });

        rig.Terminator.ForgetDonor(UnansweredReuse("job-r5", "deg-job-vma"));

        rig.Terminator.DonorCount.ShouldBe(1);
    }

    [Fact]
    public async Task D970r3_restore_outcomes_nobody_took_are_dropped_with_the_donors_that_expired()
    {
        // A Cancelled row's outcomes are kept for the cancel to annotate; a cancel that threw first never takes them.
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-31T08:00:00Z"), timestampFollowsClock: true);
        var rig = new Rig(new FakeGcp());
        rig.Settings.TimeProvider = clock;
        var labels = new Dictionary<string, string> { ["job-id"] = "job-donor" };
        var scope = new CloudScope("my-project");
        rig.Terminator.NoteDonor(scope, "job-leaked", "deg-job-gone", ZoneA, labels);
        await rig.Terminator.RestoreAndNoteAsync("job-leaked", "deg-job-gone", ZoneA); // the VM is not there: a Gone outcome is noted
        rig.Terminator.DonorStateCount.ShouldBe(1, "the outcome is remembered");

        clock.Advance(TimeSpan.FromHours(2));
        rig.Terminator.NoteDonor(scope, "job-new", "deg-job-gone", ZoneA, labels);

        rig.Terminator.DonorStateCount.ShouldBe(1, "the expired outcome is dropped; only the new donor remains");
    }
}
