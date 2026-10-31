using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #970 through the whole runner: a reuse whose start fails, or a run that fails after the donor was relabelled, puts the donor's ORIGINAL labels back once it is stopped, records it on the row,
/// and a run that SUCCEEDS on the borrowed VM keeps it (the VM is that run's now). The observable is the donor's label set and the jobs that find it by label.
/// </summary>
public class DonorLabelRestoreRunnerTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

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

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, Action<DonorRestoreOutcome>? onRestore = null) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        OnDonorRestore = onRestore,
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = TimeSpan.FromSeconds(20),
        CallTimeout = TimeSpan.FromSeconds(10),
        CreateTimeout = TimeSpan.FromSeconds(10),
        CreateSettleTimeout = TimeSpan.FromSeconds(2),
        UploadTimeout = TimeSpan.FromSeconds(10),
        BootTimeout = TimeSpan.FromSeconds(10),
        LifecycleTimeout = TimeSpan.FromMilliseconds(500),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LateCreateGrace = TimeSpan.FromMilliseconds(20),
    };

    private static async Task<IReadOnlyDictionary<string, string>> SeedDonorAsync(FakeGcp gcp, InMemoryRunRepository repo, string oldJob)
    {
        var spec = Spec(oldJob);
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        await gcp.StopVmAsync(spec.VmName, Zone, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord(oldJob, JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
        return (await gcp.GetVmAsync(spec.VmName, Zone, CancellationToken.None))!.Labels!;
    }

    private static string Flat(IReadOnlyDictionary<string, string>? labels)
        => string.Join(",", labels!.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    [Fact]
    public async Task A_start_that_fails_with_a_stockout_puts_the_donors_own_labels_back_before_the_fallback_create()
    {
        var gcp = new FakeGcp().WithReuseStartFailure(CloudErrorKind.Stockout);
        var repo = new InMemoryRunRepository();
        var donorLabels = await SeedDonorAsync(gcp, repo, "job-old970r1");

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-n970r1", Spec("job-n970r1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var donor = (await gcp.GetVmAsync("deg-job-old970r1", Zone, CancellationToken.None))!;
        donor.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        Flat(donor.Labels).ShouldBe(Flat(donorLabels), "the failed reuse left the donor under the new job's labels; they are the donor's again");
        gcp.DeleteAttempts.ShouldBeEmpty();
        (await gcp.FindByJobIdAsync("job-old970r1", CancellationToken.None)).ShouldContain(v => v.Name == "deg-job-old970r1", "the donor's own keep-alive sweep finds it by label");
        (await gcp.FindByJobIdAsync("job-n970r1", CancellationToken.None)).ShouldNotContain(v => v.Name == "deg-job-old970r1", "the new job's adoption, key-one and lifecycle sweeps no longer see the donor");
    }

    [Fact]
    public async Task A_start_that_fails_with_billing_off_fails_the_run_and_the_donor_is_restored_by_the_watch_once_the_start_is_judged_never_to_land()
    {
        // Billing is not a class that proves the start was never sent (it can be the answer of the poll AFTER the POST), so the donor is held and the late watch hands it back.
        var gcp = new FakeGcp().WithReuseStartFailure(CloudErrorKind.Billing);
        var repo = new InMemoryRunRepository();
        var donorLabels = await SeedDonorAsync(gcp, repo, "job-old970r2");
        var restores = new List<DonorRestoreOutcome>();
        var runner = Runner(gcp, repo, restores.Add);

        var result = await runner.RunAsync(TestInputs.Request("job-n970r2", Spec("job-n970r2")), CancellationToken.None);
        await runner.WhenReuseWatchesEndedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("billing");
        var donor = (await gcp.GetVmAsync("deg-job-old970r2", Zone, CancellationToken.None))!;
        Flat(donor.Labels).ShouldBe(Flat(donorLabels));
        donor.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty();
        restores.ShouldHaveSingleItem().State.ShouldBe(DonorRestoreState.Restored);
    }

    [Fact]
    public async Task A_run_that_fails_after_the_donor_was_relabelled_and_started_restores_the_donor_when_it_ends_the_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed);
        var repo = new InMemoryRunRepository();
        var donorLabels = await SeedDonorAsync(gcp, repo, "job-old970r3");

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-n970r3", Spec("job-n970r3")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.ReuseAttempts.ShouldBe(1);
        var donor = (await gcp.GetVmAsync("deg-job-old970r3", Zone, CancellationToken.None))!;
        donor.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        Flat(donor.Labels).ShouldBe(Flat(donorLabels), "a run that failed on a borrowed VM hands it back to the run it came from");
        gcp.DeleteAttempts.ShouldBeEmpty("#999: never deleted");
        repo.AllRecordedInOrder.Last(r => r.JobId == "job-n970r3").ErrorDetail.ShouldNotBeNull().ShouldContain("earlier labels were restored");
    }

    [Fact]
    public async Task A_run_that_succeeds_on_the_borrowed_VM_keeps_it_under_its_own_labels()
    {
        // The restore is for failed and cancelled runs only: a finished run's kept-alive VM is that run's, found by its own job id (Hard Rule 9).
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        await SeedDonorAsync(gcp, repo, "job-old970r4");

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-n970r4", Spec("job-n970r4")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        (await gcp.GetVmAsync("deg-job-old970r4", Zone, CancellationToken.None))!.Labels!["job-id"].ShouldBe("job-n970r4");
        gcp.RestoreAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_restore_that_fails_does_not_change_the_runs_failure_and_the_row_names_the_class_and_the_Cloud_page()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed).WithRestoreFailure(CloudErrorKind.Permission);
        var repo = new InMemoryRunRepository();
        await SeedDonorAsync(gcp, repo, "job-old970r5");

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-n970r5", Spec("job-n970r5")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        var donor = (await gcp.GetVmAsync("deg-job-old970r5", Zone, CancellationToken.None))!;
        donor.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        donor.Labels!["job-id"].ShouldBe("job-n970r5", "the restore failed: the VM keeps the new job's labels and the by-name guards protect it");
        gcp.DeleteAttempts.ShouldBeEmpty();
        var detail = repo.AllRecordedInOrder.Last(r => r.JobId == "job-n970r5").ErrorDetail;
        detail.ShouldNotBeNull().ShouldContain("could not be restored (permission)");
        detail.ShouldContain("Cloud page");
    }
}
