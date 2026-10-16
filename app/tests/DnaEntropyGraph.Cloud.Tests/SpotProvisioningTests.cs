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

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo)
        => new(gcp, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(200),
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
    public async Task Only_one_automatic_retry_is_made_even_when_the_Spot_create_is_preempted_again()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        var gcp = new FakeGcp(time).WithSpotPreemptions(5);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-s6", Spec("job-s6", spot: true)), CancellationToken.None);

        // The retry is on-demand, so nothing can preempt it: the run completes after exactly two creates.
        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Count.ShouldBe(2);
        LastRow(repo, "job-s6").PreemptionCount.ShouldBe(1);
    }
}
