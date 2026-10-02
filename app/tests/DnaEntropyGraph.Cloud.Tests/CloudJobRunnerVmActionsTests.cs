using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #428: the pieces JobEngine needs from <see cref="CloudJobRunner"/>
/// once it delegates to it: VM-scoped Stop/Delete by job id (the run page's
/// "Stop VM now" / "Delete VM now"), a safe Cancel for a job that never
/// started, a runner that turns an unexpected exception into a recorded
/// Failed phase (Hard Rule 11: every run ends in a recorded terminal state),
/// and a repository row that keeps the columns the engine wrote first.
/// </summary>
public class CloudJobRunnerVmActionsTests
{
    private static VmSpec Spec(string jobId, string installationId = "install-1") => new(
        ProjectId: "my-project",
        InstallationId: installationId,
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "keep",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudJobRequest Request(string jobId, AfterTaskAction afterTask = AfterTaskAction.KeepAlive, string installationId = "install-1") => new(
        JobId: jobId,
        Spec: Spec(jobId, installationId),
        Zones: ["us-central1-a"],
        InputObjectKeys: [],
        OutputObjectKeys: [],
        AfterTask: afterTask);

    private static CloudJobRunner NewRunner(FakeGcp gcp, InMemoryRunRepository? repo = null)
        => new(gcp, gcp, gcp, gcp, repo ?? new InMemoryRunRepository());

    [Fact]
    public async Task StopVm_stops_the_VM_found_by_job_id()
    {
        var gcp = new FakeGcp();
        var runner = NewRunner(gcp);
        await runner.RunAsync(Request("job-1"), CancellationToken.None);
        (await gcp.GetVmAsync("deg-job-1", "us-central1-a", CancellationToken.None))!.Status.ShouldBe("RUNNING");

        await runner.StopVmAsync("job-1", CancellationToken.None);

        (await gcp.GetVmAsync("deg-job-1", "us-central1-a", CancellationToken.None))!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task DeleteVm_deletes_the_VM_found_by_job_id()
    {
        var gcp = new FakeGcp();
        var runner = NewRunner(gcp);
        await runner.RunAsync(Request("job-2"), CancellationToken.None);

        await runner.DeleteVmAsync("job-2", CancellationToken.None);

        (await gcp.FindByJobIdAsync("job-2", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Stop_and_delete_for_a_job_with_no_VM_do_nothing_and_do_not_throw()
    {
        var runner = NewRunner(new FakeGcp());

        await Should.NotThrowAsync(() => runner.StopVmAsync("no-such-job", CancellationToken.None));
        await Should.NotThrowAsync(() => runner.DeleteVmAsync("no-such-job", CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_a_job_that_never_started_records_nothing_and_does_not_throw()
    {
        var repo = new InMemoryRunRepository();
        var runner = NewRunner(new FakeGcp(), repo);

        await Should.NotThrowAsync(() => runner.CancelAsync("never-started", CancellationToken.None));

        repo.AllRecordedInOrder.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_run_still_in_Draft_records_Cancelled()
    {
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-d", JobPhase.Draft, DateTimeOffset.UtcNow), CancellationToken.None);
        var runner = NewRunner(new FakeGcp(), repo);

        await runner.CancelAsync("job-d", CancellationToken.None);

        (await runner.GetPhaseAsync("job-d", CancellationToken.None)).ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task An_unexpected_exception_is_recorded_as_Failed_not_left_in_the_last_phase()
    {
        // An invalid label value makes VmSpec.EnsurePreconditions throw
        // InvalidOperationException inside CreateVmAsync: not a
        // CloudOperationException, so nothing in the ladder classifies it.
        var repo = new InMemoryRunRepository();
        var runner = NewRunner(new FakeGcp(), repo);

        var result = await runner.RunAsync(Request("job-3", installationId: "Not A Label"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Other);
        result.FailureMessage.ShouldNotBeNullOrWhiteSpace();
        (await runner.GetPhaseAsync("job-3", CancellationToken.None)).ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_recorded_as_Failed()
    {
        var runner = NewRunner(new FakeGcp());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunAsync(Request("job-4"), cts.Token));
        (await runner.GetPhaseAsync("job-4", CancellationToken.None)).ShouldNotBe(JobPhase.Failed);
    }

    [Fact]
    public async Task Phase_updates_keep_the_columns_an_earlier_writer_stored_and_stamp_the_finish_time()
    {
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(
            new RunRecord("job-5", JobPhase.Draft, DateTimeOffset.UtcNow, Name: "my run", OptionsJson: "{\"a\":1}", ProjectId: "my-project", InstallationId: "install-1"),
            CancellationToken.None);
        var runner = NewRunner(new FakeGcp(), repo);

        await runner.RunAsync(Request("job-5", AfterTaskAction.Stop), CancellationToken.None);

        var records = repo.AllRecordedInOrder.Where(r => r.JobId == "job-5").ToList();
        records.Count.ShouldBeGreaterThan(2);
        records.ShouldAllBe(r => r.Name == "my run" && r.OptionsJson == "{\"a\":1}" && r.ProjectId == "my-project");
        records[^1].Phase.ShouldBe(JobPhase.Completed);
        records[^1].FinishedAt.ShouldNotBeNull();
        records[^2].FinishedAt.ShouldBeNull();
    }
}

/// <summary>Issue #261: the runner attaches the startup script and its per-job attributes to the VM it creates.</summary>
public class CloudJobRunnerStartupMetadataTests
{
    private const string Digest = "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static CloudJobRequest Request(string jobId, string? image, string machineType = "g2-standard-8") => new(
        JobId: jobId,
        Spec: new VmSpec("my-project", "install-1", jobId, "evo2_7b", "0.1.0", "stop", machineType, TimeSpan.FromHours(2), "DELETE"),
        Zones: ["us-central1-a"],
        InputObjectKeys: [],
        OutputObjectKeys: [],
        AfterTask: AfterTaskAction.Stop,
        WorkerImage: image);

    [Fact]
    public async Task A_run_with_a_worker_image_creates_the_VM_with_the_startup_script_and_attributes()
    {
        var gcp = new FakeGcp();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, new InMemoryRunRepository());

        var result = await runner.RunAsync(Request("job-m1", Digest), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        var metadata = gcp.CreatedSpecs.Single().Metadata;
        metadata.ShouldNotBeNull();
        metadata!["startup-script"].ShouldBe(StartupMetadata.Script);
        metadata["deg-job-id"].ShouldBe("job-m1");
        metadata["deg-worker-image"].ShouldBe(Digest);
        metadata["deg-bucket"].ShouldBe(await gcp.EnsureBucketAsync("my-project", CancellationToken.None));
        metadata["deg-expect-gpu"].ShouldBe("true");
        metadata["deg-max-run-min"].ShouldBe("120");
    }

    [Fact]
    public async Task A_CPU_machine_type_does_not_expect_a_GPU()
    {
        var gcp = new FakeGcp();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, new InMemoryRunRepository());

        await runner.RunAsync(Request("job-m2", Digest, machineType: "e2-standard-4"), CancellationToken.None);

        gcp.CreatedSpecs.Single().Metadata!["deg-expect-gpu"].ShouldBe("false");
    }

    [Fact]
    public async Task A_run_without_a_worker_image_adds_no_metadata()
    {
        var gcp = new FakeGcp();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, new InMemoryRunRepository());

        await runner.RunAsync(Request("job-m3", null), CancellationToken.None);

        gcp.CreatedSpecs.Single().Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task An_invalid_worker_image_fails_the_run_before_any_VM_is_created()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, repo);

        var result = await runner.RunAsync(Request("job-m4", "ghcr.io/x/y:latest"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        gcp.CreatedSpecs.ShouldBeEmpty();
        (await runner.GetPhaseAsync("job-m4", CancellationToken.None)).ShouldBe(JobPhase.Failed);
    }
}
