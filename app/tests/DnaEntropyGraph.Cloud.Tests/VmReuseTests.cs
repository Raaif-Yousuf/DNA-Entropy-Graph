using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #94: the next job starts this installation's stopped VM (a fresh label, metadata and start) instead of paying for a new
/// VM and image pull, and a start that fails is "provision needed", never a failed run. Every test asserts the VMs that remain
/// in the fake project, because a reuse that left a second VM, or ended the wrong one, is the bug.
/// </summary>
public class VmReuseTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId, string model = "evo2_7b", string machine = "g2-standard-8", string installation = "install-1") => new(
        ProjectId: Project,
        InstallationId: installation,
        JobId: jobId,
        Model: model,
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: machine,
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = TimeSpan.FromSeconds(20),
        CallTimeout = TimeSpan.FromSeconds(10),
        CreateTimeout = TimeSpan.FromSeconds(10),
        CreateSettleTimeout = TimeSpan.FromSeconds(2),
        UploadTimeout = TimeSpan.FromSeconds(10),
        BootTimeout = TimeSpan.FromSeconds(10),
        LifecycleTimeout = TimeSpan.FromMilliseconds(500),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
    };

    /// <summary>A VM an earlier job left stopped (the default after a run) whose run row ended in <paramref name="phase"/>.</summary>
    private static async Task SeedIdleVmAsync(FakeGcp gcp, InMemoryRunRepository repo, string oldJob, VmSpec? spec = null, JobPhase phase = JobPhase.Completed, bool stopped = true)
    {
        spec ??= Spec(oldJob);
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        if (stopped)
        {
            await gcp.StopVmAsync(spec.VmName, Zone, CancellationToken.None);
        }

        await repo.UpsertAsync(new RunRecord(oldJob, phase, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private static async Task<IReadOnlyList<VmDescriptor>> VmsAsync(FakeGcp gcp) => await gcp.ListAppVmsAsync(CancellationToken.None);

    [Fact]
    public async Task The_second_job_starts_the_stopped_VM_instead_of_creating_one_and_leaves_it_stopped_under_its_old_name()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        result.FinalPhase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Completed, result.FailureMessage);
        gcp.ReuseAttempts.ShouldBe(1);
        gcp.CreateAttempts.ShouldBe(1, "only the seed: the run created a second VM instead of reusing");
        var vms = await VmsAsync(gcp);
        var vm = vms.ShouldHaveSingleItem();
        vm.Name.ShouldBe("deg-job-old");
        vm.Labels!["job-id"].ShouldBe("job-new", "discovery is by label: the reused VM must now belong to the new job (Hard Rule 9)");
        vm.Status.ShouldBe("STOPPED", "the after-task stop must be applied to, and verified on, the reused VM under its real name");
    }

    [Fact]
    public async Task A_start_that_fails_with_a_stockout_falls_back_to_creating_a_new_VM_and_the_borrowed_one_is_stopped_never_deleted()
    {
        // Issue #999: the donor is the earlier run's VM (its disk, its outputs). A failed start leaves it STOPPED under its own name; only a VM this run created is ever deleted.
        var gcp = new FakeGcp().WithReuseStartFailure(CloudErrorKind.Stockout);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        result.FinalPhase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Completed, result.FailureMessage);
        gcp.ReuseAttempts.ShouldBe(1);
        gcp.CreateAttempts.ShouldBe(2, "the seed and the fallback create");
        gcp.DeleteAttempts.ShouldBeEmpty("#999: the relabelled donor belongs to the earlier run and is never deleted by this one");
        var vms = (await VmsAsync(gcp)).ToDictionary(v => v.Name);
        vms.Keys.ShouldBe(["deg-job-old", "deg-job-new"], ignoreOrder: true);
        vms["deg-job-old"].Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        vms["deg-job-new"].Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_start_that_fails_with_billing_off_aborts_the_run_and_leaves_the_borrowed_VM_stopped()
    {
        var gcp = new FakeGcp().WithReuseStartFailure(CloudErrorKind.Billing);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        result.FinalPhase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Failed);
        result.FailureCode.ShouldBe("billing");
        gcp.CreateAttempts.ShouldBe(1, "a project-wide failure must not be answered by creating a VM");
        gcp.DeleteAttempts.ShouldBeEmpty("#999: the donor is another run's VM: stopped, never deleted");
        (await VmsAsync(gcp)).ShouldHaveSingleItem().Status.ShouldBeOneOf("STOPPED", "TERMINATED");
    }

    [Fact]
    public async Task A_VM_for_another_model_is_not_reused()
        => await AssertNotReusedAsync(Spec("job-old", model: "evo2_1b"));

    [Fact]
    public async Task A_VM_of_another_machine_type_is_not_reused()
        => await AssertNotReusedAsync(Spec("job-old", machine: "a2-highgpu-1g"));

    [Fact]
    public async Task Another_installations_VM_is_never_touched()
        => await AssertNotReusedAsync(Spec("job-old", installation: "install-2"));

    [Fact]
    public async Task A_stopped_VM_of_a_run_that_is_still_underway_is_not_reused()
        => await AssertNotReusedAsync(Spec("job-old"), JobPhase.Running);

    [Fact]
    public async Task A_VM_that_is_still_running_is_not_reused()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Done);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old", stopped: false);

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        result.FinalPhase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Completed, result.FailureMessage);
        gcp.ReuseAttempts.ShouldBe(0);
        (await VmsAsync(gcp)).Select(v => v.Name).Order().ShouldBe(["deg-job-new", "deg-job-old"]);
    }

    private static async Task AssertNotReusedAsync(VmSpec oldSpec, JobPhase oldPhase = JobPhase.Completed)
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old", oldSpec, oldPhase);

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        result.FinalPhase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Completed, result.FailureMessage);
        gcp.ReuseAttempts.ShouldBe(0);
        gcp.CreateAttempts.ShouldBe(2);
        var old = (await VmsAsync(gcp)).Single(v => v.Name == "deg-job-old");
        old.Status.ShouldBe("STOPPED");
        old.Labels!["job-id"].ShouldBe("job-old", "a VM that is not ours to reuse must keep its label");
    }

    [Fact]
    public async Task A_run_resumed_after_the_reuse_adopts_the_relabelled_VM_by_label_and_ends_it_under_its_old_name()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        await SeedIdleVmAsync(gcp, repo, "job-old");
        var request = TestInputs.Request("job-new", Spec("job-new"));
        await gcp.ReuseVmAsync(request.Spec, "deg-job-old", Zone, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-new", DnaEntropyGraph.Core.JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);
        gcp.PutObject(FakeGcp.BucketName(Project), "jobs/job-new/manifest.json", """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray());
        var attempts = gcp.ReuseAttempts;
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo, (_, phase) =>
        {
            if (phase == DnaEntropyGraph.Core.JobPhase.Running)
            {
                gcp.SimulateWorkerFinishing(request.Spec with { ExistingVmName = "deg-job-old" }, Zone);
            }
        })
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(20),
            LifecycleTimeout = TimeSpan.FromMilliseconds(500),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Completed, result.FailureMessage);
        gcp.ReuseAttempts.ShouldBe(attempts, "a resume adopts; it never reuses or creates again");
        (await VmsAsync(gcp)).ShouldHaveSingleItem().Status.ShouldBe("STOPPED");
    }
}
