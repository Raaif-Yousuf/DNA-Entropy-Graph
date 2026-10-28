using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #91: Spot provisioning. The options toggle reaches the request, the request reaches the Compute instance body
/// (provisioningModel=SPOT with instanceTerminationAction still DELETE, Hard Rule 10), and one preemption ends in exactly one
/// automatic on-demand retry recorded on the run row.
/// </summary>
public class SpotProvisioningTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId, bool spot) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE")
    {
        Spot = spot,
    };

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, TimeSpan? resultTimeout = null)
        => new(gcp, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = resultTimeout ?? TimeSpan.FromMilliseconds(200),
            PreemptionLookupInterval = TimeSpan.FromMilliseconds(1),
            CallTimeout = TimeSpan.FromMilliseconds(100),
            EnsureBucketTimeout = TimeSpan.FromMilliseconds(100),
            CreateTimeout = TimeSpan.FromMilliseconds(100),
            CreateSettleTimeout = TimeSpan.FromSeconds(2),
            UploadTimeout = TimeSpan.FromMilliseconds(100),
            BootTimeout = TimeSpan.FromMilliseconds(200),
            LifecycleTimeout = TimeSpan.FromMilliseconds(200),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    [Fact]
    public void The_Spot_option_reaches_the_VmSpec_and_is_off_by_default()
    {
        var inputs = new[] { new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.gb", "a.gb") };
        var on = CloudJobRequestFactory.Create(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", SpotVm = true }, "20260101-000000-abcdef", Project, "install-1", "0.1.0", null, inputs, @"C:\out");
        var off = CloudJobRequestFactory.Create(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }, "20260101-000000-abcdef", Project, "install-1", "0.1.0", null, inputs, @"C:\out");

        on.Spec.Spot.ShouldBeTrue();
        off.Spec.Spot.ShouldBeFalse();
        on.Spec.TerminationAction.ShouldBe("DELETE");
    }

    [Fact]
    public void A_Spot_instance_body_says_SPOT_and_keeps_DELETE_and_the_run_limit()
    {
        var spec = Spec("job-s1", spot: true);

        var body = ComputeVmShape.Build(spec, Zone, spec.ToLabels(), "sa@my-project.iam.gserviceaccount.com");

        body.Scheduling.ProvisioningModel.ShouldBe("SPOT");
        body.Scheduling.InstanceTerminationAction.ShouldBe("DELETE");
        body.Scheduling.MaxRunDuration.Seconds.ShouldBe((long)TimeSpan.FromHours(4).TotalSeconds);
    }

    [Fact]
    public void An_on_demand_instance_body_names_no_Spot_provisioning_model()
    {
        var spec = Spec("job-s2", spot: false);

        var body = ComputeVmShape.Build(spec, Zone, spec.ToLabels(), "sa@my-project.iam.gserviceaccount.com");

        body.Scheduling.ProvisioningModel.ShouldNotBe("SPOT");
    }

    [Fact]
    public async Task A_Spot_run_records_a_Spot_create_first_then_one_on_demand_create_after_one_preemption()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(1);
        var repo = new InMemoryRunRepository();
        var spec = Spec("job-s3", spot: true);

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-s3", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Select(s => s.Spot).ShouldBe([true, false]);
        gcp.CreatedSpecs.ShouldAllBe(s => s.TerminationAction == "DELETE");
        LastRow(repo, "job-s3").PreemptionCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_Spot_run_that_is_never_preempted_makes_one_Spot_create_and_records_no_preemption()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-s4", Spec("job-s4", spot: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Select(s => s.Spot).ShouldBe([true]);
        LastRow(repo, "job-s4").PreemptionCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_lost_on_demand_VM_is_never_retried_as_a_preemption()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var spec = Spec("job-s5", spot: false);
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never).WithPreemption(spec.VmName, Zone, TimeSpan.Zero);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-s5", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.CreatedSpecs.Count.ShouldBe(1);
        LastRow(repo, "job-s5").PreemptionCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_second_loss_of_the_on_demand_retry_is_not_retried_again()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(1).WithOnDemandVmLosses(1);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-s6", Spec("job-s6", spot: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.CreatedSpecs.Select(s => s.Spot).ShouldBe([true, false], "the on-demand retry is the last attempt");
        gcp.PreemptionLookups.ShouldBe(1, "an on-demand VM is never asked about preemption");
        LastRow(repo, "job-s6").PreemptionCount.ShouldBe(1);
    }

    // ---- H1: positive evidence only ----

    private static async Task<(Task<CloudJobResult> Run, string JobId)> StartRunningAsync(CloudJobRunner runner, InMemoryRunRepository repo, string jobId)
    {
        var run = Task.Run(() => runner.RunAsync(TestInputs.Request(jobId, Spec(jobId, spot: true)), CancellationToken.None), TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!repo.AllRecordedInOrder.Any(r => r.JobId == jobId && r.Phase == JobPhase.Running))
        {
            (DateTime.UtcNow < until).ShouldBeTrue("the run never reached Running");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        return (run, jobId);
    }

    [Fact]
    public async Task A_VM_the_person_deleted_mid_run_is_not_a_preemption_and_costs_no_second_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, TimeSpan.FromSeconds(30));
        var (run, jobId) = await StartRunningAsync(runner, repo, "job-h1a");

        gcp.RecordPreemptionOf($"deg-{jobId}", Zone); // Compute even has a preemption on record for this VM: the app's own Delete still wins
        await runner.DeleteVmAsync(jobId, CancellationToken.None);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.CreatedSpecs.Count.ShouldBe(1);
        LastRow(repo, jobId).PreemptionCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_cancel_mid_run_is_not_a_preemption_and_costs_no_second_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, TimeSpan.FromSeconds(30));
        var (run, jobId) = await StartRunningAsync(runner, repo, "job-h1b");

        gcp.RecordPreemptionOf($"deg-{jobId}", Zone);
        await runner.CancelAsync(jobId, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        gcp.CreatedSpecs.Count.ShouldBe(1);
        LastRow(repo, jobId).Phase.ShouldBe(JobPhase.Cancelled);
        LastRow(repo, jobId).PreemptionCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_VM_that_vanishes_with_no_preemption_record_is_not_retried()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, TimeSpan.FromSeconds(30));
        var (run, jobId) = await StartRunningAsync(runner, repo, "job-h1c");

        await gcp.DeleteVmAsync($"deg-{jobId}", Zone, CancellationToken.None); // an admin or a zone outage: Compute records no preemption
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.CreatedSpecs.Count.ShouldBe(1);
        LastRow(repo, jobId).PreemptionCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_Delete_pressed_before_this_runs_VM_existed_does_not_silence_its_retry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(1);
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);

        await runner.DeleteVmAsync("job-h1e", CancellationToken.None); // an earlier, kept-alive VM of this job was deleted by hand: nothing to do with the VM about to exist
        var result = await runner.RunAsync(TestInputs.Request("job-h1e", Spec("job-h1e", spot: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Select(s => s.Spot).ShouldBe([true, false]);
        LastRow(repo, "job-h1e").PreemptionCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_preemption_recorded_a_moment_late_is_still_found_on_the_third_look()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(1).WithPreemptionVisibleFromLookup(3);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-l3", Spec("job-l3", spot: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.PreemptionLookups.ShouldBe(3);
        gcp.CreatedSpecs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_preemption_never_recorded_is_asked_about_exactly_three_times_and_not_retried()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(1).WithPreemptionVisibleFromLookup(99);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-l4", Spec("job-l4", spot: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.PreemptionLookups.ShouldBe(3);
        gcp.CreatedSpecs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_old_preemption_record_for_the_same_name_does_not_count_for_a_new_VM()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never).WithStalePreemptionRecord("deg-job-h2b", Zone, instanceId: 1, at: DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, TimeSpan.FromSeconds(30));
        var (run, jobId) = await StartRunningAsync(runner, repo, "job-h2b");

        await gcp.DeleteVmAsync($"deg-{jobId}", Zone, CancellationToken.None); // gone with no preemption of THIS VM on record
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.CreatedSpecs.Count.ShouldBe(1);
        LastRow(repo, jobId).PreemptionCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_the_preemption_lookup_itself_fails_the_run_fails_and_nothing_is_retried()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(1).WithPreemptionLookupFailure();
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-h1d", Spec("job-h1d", spot: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.VmUnhealthy);
        gcp.CreatedSpecs.Count.ShouldBe(1);
        LastRow(repo, "job-h1d").PreemptionCount.ShouldBe(0);
    }

    // ---- H2: nothing of attempt 1 reaches attempt 2's watcher ----

    [Fact]
    public async Task The_retry_starts_with_no_status_or_progress_left_by_the_preempted_attempt()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Never).WithSpotPreemptions(1);
        var bucket = FakeGcp.BucketName(Project);
        var prefix = "jobs/job-h2/";
        gcp.PutObject(bucket, prefix + "status.json", "{\"stale\":true}"u8.ToArray());
        gcp.PutObject(bucket, prefix + "progress.jsonl", "{\"stale\":true}\n"u8.ToArray());
        var repo = new InMemoryRunRepository();

        await Runner(gcp, repo).RunAsync(TestInputs.Request("job-h2", Spec("job-h2", spot: true)), CancellationToken.None);

        gcp.CreatedSpecs.Count.ShouldBe(2);
        gcp.ObjectKeys(bucket).ShouldNotContain(prefix + "status.json");
        gcp.ObjectKeys(bucket).ShouldNotContain(prefix + "progress.jsonl");
    }

    // ---- M3 and L5: the row shows the retry ----

    [Fact]
    public async Task The_row_goes_back_to_Provisioning_for_the_retry_and_says_which_attempt_was_Spot()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(1);
        var repo = new InMemoryRunRepository();

        await Runner(gcp, repo).RunAsync(TestInputs.Request("job-m3", Spec("job-m3", spot: true)), CancellationToken.None);

        var rows = repo.AllRecordedInOrder.Where(r => r.JobId == "job-m3").ToList();
        var phases = rows.Select(r => r.Phase).ToList();
        var firstPreparing = phases.IndexOf(JobPhase.Preparing);
        firstPreparing.ShouldBeGreaterThanOrEqualTo(0);
        var restart = phases.IndexOf(JobPhase.Provisioning, firstPreparing);
        restart.ShouldBeGreaterThan(firstPreparing, "the row shows a computer being started again after the first VM was lost");
        rows[restart].IsSpot.ShouldBe(false, "the very write that sends the row back to Provisioning says the retry is not Spot");
        rows[restart].PreemptionCount.ShouldBe(1);
        rows.Any(r => r.IsSpot == true && r.Phase == JobPhase.Provisioning).ShouldBeTrue();
        rows.Last().IsSpot.ShouldBe(false);
        rows.Last().PreemptionCount.ShouldBe(1);
    }
}
