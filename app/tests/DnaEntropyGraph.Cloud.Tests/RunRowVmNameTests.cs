using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #679 (a): the run row names the VM the run actually used, which after a reuse (#94) is NOT deg-&lt;jobId&gt;.</summary>
public class RunRowVmNameTests
{
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId) => new(
        ProjectId: "my-project", InstallationId: "install-1", JobId: jobId, Model: "evo2_7b", AppVersion: "0.1.0", Lifecycle: "stop",
        MachineType: "g2-standard-8", MaxRunDuration: TimeSpan.FromHours(4), TerminationAction: "DELETE");

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, TimeProvider time) => new(gcp, gcp, gcp, gcp, gcp, repo)
    {
        TimeProvider = time,
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        CallTimeout = TimeSpan.FromSeconds(10),
        CreateTimeout = TimeSpan.FromSeconds(10),
        CreateSettleTimeout = TimeSpan.FromSeconds(5),
        UploadTimeout = TimeSpan.FromSeconds(10),
        BootTimeout = TimeSpan.FromSeconds(10),
        LifecycleTimeout = TimeSpan.FromSeconds(1),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
    };

    private static async Task<RunRecord> RowAfterRunAsync(bool reuse, Action<InMemoryRunRepository>? rig = null)
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Done);
        var repo = new InMemoryRunRepository();
        rig?.Invoke(repo);
        if (reuse)
        {
            var old = Spec("job-old");
            await gcp.CreateVmAsync(old, Zone, CancellationToken.None);
            await gcp.StopVmAsync(old.VmName, Zone, CancellationToken.None);
            await repo.UpsertAsync(new RunRecord("job-old", JobPhase.Completed, time.GetUtcNow()), CancellationToken.None);
        }

        var spec = Spec("job-new");
        await repo.UpsertAsync(new RunRecord("job-new", JobPhase.Draft, time.GetUtcNow(), Target: "cloud"), CancellationToken.None);

        var result = await Runner(gcp, repo, time).RunAsync(TestInputs.Request("job-new", spec), CancellationToken.None);
        result.FailureCode.ShouldBeNull(result.FailureMessage);
        if (reuse)
        {
            gcp.ReuseAttempts.ShouldBe(1, "the premise: this run reused the stopped VM");
        }

        return (await repo.GetAllAsync(CancellationToken.None)).Last(r => r.JobId == "job-new");
    }

    [Fact]
    public async Task A_run_that_reused_a_stopped_VM_records_that_VMs_name()
        => (await RowAfterRunAsync(reuse: true)).VmName.ShouldBe("deg-job-old");

    [Fact]
    public async Task A_run_that_made_a_new_VM_records_its_name()
        => (await RowAfterRunAsync(reuse: false)).VmName.ShouldBe("deg-job-new");

    // Review of #679: the name write sits between "the VM exists" and "wait for it". A throw there must not abandon a live VM.
    [Fact]
    public async Task A_failed_write_of_the_VM_name_does_not_stop_the_run_and_the_VM_still_ends_terminal()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Done);
        var repo = new InMemoryRunRepository();
        var thrown = 0;
        repo.BeforeUpsert = (run, _) =>
        {
            if (run.VmName is not null && thrown++ == 0)
            {
                throw new IOException("disk full");
            }

            return Task.CompletedTask;
        };
        await repo.UpsertAsync(new RunRecord("job-new", JobPhase.Draft, time.GetUtcNow(), Target: "cloud"), CancellationToken.None);

        var result = await Runner(gcp, repo, time).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        thrown.ShouldBe(1, "the premise: the name write was attempted and threw");
        result.FailureCode.ShouldBeNull(result.FailureMessage);
        (await gcp.GetVmAsync("deg-job-new", Zone, CancellationToken.None))!.Status.ShouldBe("STOPPED", "the run ended and stopped its VM as the row's Stop lifecycle says");
    }

    // Review of #679: an adopted VM whose name cannot be read must keep the row's name, not be overwritten with the planned NEW name.
    [Fact]
    public async Task An_adopted_run_that_finds_no_VM_does_not_overwrite_the_row_with_the_planned_name()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        var gcp = new FakeGcp(time).WithWorker(FakeWorkerMode.Done);
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-new", JobPhase.Running, time.GetUtcNow(), Target: "cloud", VmName: "deg-job-old", Bucket: FakeGcp.BucketName("my-project")), CancellationToken.None);
        gcp.PutObject(FakeGcp.BucketName("my-project"), "jobs/job-new/result.json", System.Text.Encoding.UTF8.GetBytes("{}"));

        await Runner(gcp, repo, time).RunAsync(TestInputs.Request("job-new", Spec("job-new")), CancellationToken.None);

        repo.AllRecordedInOrder.Where(r => r.JobId == "job-new").Select(r => r.VmName).ShouldNotContain("deg-job-new", "the planned name of a VM that was never made is not the VM the run used");
    }}
