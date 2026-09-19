using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #58's own Done-when, one test class per named scenario: "Runner
/// drives FakeGcp happy path, stockout ladder, cancel, preemption, app
/// crash + reattach." Issue #257's reconciler (<see cref="IComputeGateway.FindByJobIdAsync"/>
/// called first in every provisioning attempt) is what makes the
/// crash-and-reattach tests here safe rather than theoretical - see
/// <see cref="A_crash_after_provisioning_resumes_without_creating_a_second_vm"/>.
/// </summary>
public class CloudJobRunnerTests
{
    private static VmSpec ValidSpec(string jobId, string projectId = "my-project") => new(
        ProjectId: projectId,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudJobRequest ValidRequest(string jobId, IReadOnlyList<string>? zones = null, AfterTaskAction afterTask = AfterTaskAction.Stop) => new(
        JobId: jobId,
        Spec: ValidSpec(jobId),
        Zones: zones ?? ["us-central1-a"],
        InputObjectKeys: ["jobs/" + jobId + "/input/input.gb"],
        OutputObjectKeys: ["jobs/" + jobId + "/output/track.bedgraph"],
        AfterTask: afterTask);

    private static CloudJobRunner NewRunner(FakeGcp gcp, InMemoryRunRepository? repo = null, Action<string, JobPhase>? onPhaseChanged = null)
        => new(gcp, gcp, gcp, gcp, repo ?? new InMemoryRunRepository(), onPhaseChanged);

    [Fact]
    public async Task Happy_path_reaches_Completed_and_records_every_phase_in_order()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var seen = new List<JobPhase>();
        var runner = NewRunner(gcp, repo, (_, phase) => seen.Add(phase));

        var result = await runner.RunAsync(ValidRequest("job-1"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        seen.ShouldBe([
            JobPhase.Validating,
            JobPhase.Uploading,
            JobPhase.Provisioning,
            JobPhase.Preparing,
            JobPhase.Running,
            JobPhase.Finalizing,
            JobPhase.Downloading,
            JobPhase.Completed,
        ]);
    }

    [Fact]
    public async Task Every_phase_change_is_committed_to_the_repository_before_the_callback_fires()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var sawInRepoWhenCallbackFired = new List<bool>();
        var runner = NewRunner(gcp, repo, (jobId, phase) =>
        {
            // The Done-when's own words: "every phase change committed to
            // SQLite before the UI is told." Prove it from the callback's
            // own point of view, not just by calling things in that order.
            sawInRepoWhenCallbackFired.Add(repo.AllRecordedInOrder.Any(r => r.JobId == jobId && r.Phase == phase));
        });

        await runner.RunAsync(ValidRequest("job-2"), CancellationToken.None);

        sawInRepoWhenCallbackFired.ShouldAllBe(seen => seen);
        sawInRepoWhenCallbackFired.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_stockout_ladder_tries_the_next_zone_and_still_reaches_Completed()
    {
        var gcp = new FakeGcp()
            .WithZoneStockout("us-central1-a")
            .WithZoneStockout("us-central1-b");
        var runner = NewRunner(gcp);

        var result = await runner.RunAsync(ValidRequest("job-3", ["us-central1-a", "us-central1-b", "us-central1-c"]), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        (await gcp.FindByJobIdAsync("job-3", CancellationToken.None)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_project_wide_failure_at_create_time_aborts_the_ladder_at_the_first_zone_instead_of_trying_the_rest()
    {
        // Permission/org-policy are checked only inside CreateVmAsync's own
        // ThrowIfProjectWide, never by the Billing/API preflight checks
        // above - this deliberately exercises the LADDER's own
        // abort-vs-continue rule (docs/cloud_design.md section 5), not
        // PreflightAsync's earlier, separate check of the same table.
        var gcp = new FakeGcp().WithPermissionDenied("my-project");
        var runner = NewRunner(gcp);

        var result = await runner.RunAsync(ValidRequest("job-4", ["us-central1-a", "us-central1-b"]), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Permission);
        (await gcp.FindByJobIdAsync("job-4", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Preflight_fails_the_run_before_any_upload_when_the_project_is_not_active()
    {
        var gcp = new FakeGcp().WithProjectState("my-project", ProjectLifecycleState.NotFound);
        var runner = NewRunner(gcp);

        var result = await runner.RunAsync(ValidRequest("job-5"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Permission);
        // Never even reached Provisioning - nothing was created.
        (await gcp.FindByJobIdAsync("job-5", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Preflight_fails_the_run_when_gpu_quota_is_zero_everywhere()
    {
        var gcp = new FakeGcp().WithAllRegionsGpuCap(0);
        var runner = NewRunner(gcp);

        var result = await runner.RunAsync(ValidRequest("job-5b"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Quota);
    }

    [Fact]
    public async Task Cancel_deletes_the_provisioned_vm_and_reaches_Cancelled()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = NewRunner(gcp, repo);
        var request = ValidRequest("job-6");

        // Drive the runner far enough to have a real VM (call Provisioning
        // by hand rather than the whole happy path, since CancelAsync needs
        // something to cancel).
        await gcp.CreateVmAsync(request.Spec, "us-central1-a", CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-6", JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);

        await runner.CancelAsync("job-6", CancellationToken.None);

        (await runner.GetPhaseAsync("job-6", CancellationToken.None)).ShouldBe(JobPhase.Cancelled);
        (await gcp.FindByJobIdAsync("job-6", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_on_a_job_already_in_a_terminal_phase_is_a_no_op()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-7", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);
        var runner = NewRunner(gcp, repo);

        await runner.CancelAsync("job-7", CancellationToken.None);

        (await runner.GetPhaseAsync("job-7", CancellationToken.None)).ShouldBe(JobPhase.Completed);
    }

    [Fact]
    public async Task A_vm_discovered_preempted_fails_the_run_instead_of_reporting_success()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-19T08:00:00Z"));
        var jobId = "job-8";
        var spec = ValidSpec(jobId);
        var gcp = new FakeGcp(time).WithPreemption(spec.VmName, "us-central1-a", TimeSpan.Zero);
        var runner = NewRunner(gcp);

        var result = await runner.RunAsync(ValidRequest(jobId), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task A_crash_after_provisioning_resumes_without_creating_a_second_vm()
    {
        // Issue #58's own observable: "Disposing the runner mid-run and
        // constructing a new reconciler resumes the same job to Completed
        // in the fake." Issue #257's mechanism is what makes this safe:
        // RunnerB's ProvisionAsync calls FindByJobIdAsync FIRST and adopts
        // what RunnerA already created, rather than risking a second zone's
        // create succeeding independently (issue #389).
        //
        // RunnerA's original attempt lands in the LADDER'S SECOND zone
        // ("us-central1-b" - as if zone A had stocked out on the original
        // attempt), deliberately NOT the first zone the ladder tries. This
        // is what makes the test decisive: without reconciliation, RunnerB
        // would restart the ladder at zone A, which has nothing scripted to
        // fail it, and would happily create a SECOND, independent VM there
        // - exactly issue #389's leak - rather than merely re-discovering
        // the same zone by coincidence.
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = ValidRequest("job-9", ["us-central1-a", "us-central1-b"]);

        var runnerA = NewRunner(gcp, repo);
        await runnerA.UploadInputsAsync(request, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-9", JobPhase.Validating, DateTimeOffset.UtcNow), CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-9", JobPhase.Uploading, DateTimeOffset.UtcNow), CancellationToken.None);
        await gcp.CreateVmAsync(request.Spec, "us-central1-b", CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-9", JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);
        // "Disposing the runner mid-run": runnerA is simply never touched again.

        var runnerB = NewRunner(gcp, repo);
        var result = await runnerB.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        var vms = await gcp.FindByJobIdAsync("job-9", CancellationToken.None);
        vms.Count.ShouldBe(1);
        vms[0].Zone.ShouldBe("us-central1-b");
    }

    [Fact]
    public async Task Resuming_a_job_already_in_a_terminal_phase_does_not_re_run_anything()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-10", JobPhase.Failed, DateTimeOffset.UtcNow), CancellationToken.None);
        var runner = NewRunner(gcp, repo);

        var result = await runner.RunAsync(ValidRequest("job-10"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        (await gcp.FindByJobIdAsync("job-10", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AfterTask_stop_verifies_the_vm_actually_reached_STOPPED_hard_rule_11()
    {
        var gcp = new FakeGcp();
        var runner = NewRunner(gcp);

        var result = await runner.RunAsync(ValidRequest("job-11", afterTask: AfterTaskAction.Stop), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        var vm = await gcp.GetVmAsync("deg-job-11", "us-central1-a", CancellationToken.None);
        vm!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task AfterTask_delete_leaves_no_vm_behind()
    {
        var gcp = new FakeGcp();
        var runner = NewRunner(gcp);

        var result = await runner.RunAsync(ValidRequest("job-12", afterTask: AfterTaskAction.Delete), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        (await gcp.GetVmAsync("deg-job-12", "us-central1-a", CancellationToken.None)).ShouldBeNull();
    }
}
