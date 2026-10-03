using System.Security.Cryptography;
using System.Text;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Round 2 of the cold review of the #460 transfer work: every failure after a VM exists ends the VM, a VM
/// that is still booting is waited for, nothing hangs on a create or a call that ignores its token, a finished
/// job is never provisioned again, the output folder is proven before anything is billed, the run record never
/// carries a file name, worker codes with their own user action get their own copy, and partial files are kept.
/// </summary>
public class CloudJobRunnerHardeningTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId, TimeSpan? maxRun = null) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: maxRun ?? TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, Action<string, JobPhase>? onPhase = null, TimeSpan? resultTimeout = null, bool defaultWait = false)
        => new(gcp, gcp, gcp, gcp, repo, onPhase)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = defaultWait ? null : resultTimeout ?? TimeSpan.FromMilliseconds(80),
            CallTimeout = TimeSpan.FromSeconds(1),
            CreateTimeout = TimeSpan.FromSeconds(1),
            CreateSettleTimeout = TimeSpan.FromMilliseconds(100),
            UploadTimeout = TimeSpan.FromSeconds(1),
            BootTimeout = TimeSpan.FromSeconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    private static async Task<string?> VmStatusAsync(FakeGcp gcp, string jobId)
        => (await gcp.GetVmAsync($"deg-{jobId}", Zone, CancellationToken.None))?.Status;

    private static string TrackPath(InMemoryRunRepository repo, string jobId, string input = "seq")
        => Path.Combine(LastRow(repo, jobId).OutputDir ?? "no-output-dir", input, input + ".bedgraph");

    private static async Task<T> WithinAsync<T>(Task<T> task, string because)
    {
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        winner.ShouldBeSameAs(task, because);
        return await task;
    }

    // ---- A: every failure after create ends the VM; a booting VM is waited for ----

    [Fact]
    public async Task A_hard_failure_on_the_first_look_after_create_ends_the_VM_and_records_Failed()
    {
        var gcp = new FakeGcp().WithGetVmFailure(new CloudError("IAM_PERMISSION_DENIED", 403, "Required 'compute.instances.get' permission."), 1);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-a1", Spec("job-a1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe("permission");
        LastRow(repo, "job-a1").Phase.ShouldBe(JobPhase.Failed);
        (await VmStatusAsync(gcp, "job-a1")).ShouldBe("STOPPED", "a failure after the VM exists must end it (Hard Rule 11)");
    }

    [Fact]
    public async Task A_transient_failure_on_the_first_look_after_create_does_not_end_a_run_that_is_fine()
    {
        // Deliberately not "Failed": the VM and the worker carry on without the app, so one blip must not cost a second run.
        var gcp = new FakeGcp().WithGetVmFailures(503, 1);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-a1t", Spec("job-a1t")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public async Task An_unexpected_exception_after_the_VM_exists_still_ends_the_VM()
    {
        // The injector was a throwing UI callback; a notification failure is no longer a run failure (it must never change
        // the outcome, see CloudJobRunnerGuaranteesTests), so the unexpected exception is a repository that fails at Running.
        var gcp = new FakeGcp();
        var repo = new FailingAtRunningRepository(new InMemoryRunRepository());
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(80),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = await runner.RunAsync(TestInputs.Request("job-a4", Spec("job-a4")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        (await VmStatusAsync(gcp, "job-a4")).ShouldBe("STOPPED");
    }

    private sealed class FailingAtRunningRepository(InMemoryRunRepository inner) : IRunRepository
    {
        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
            => run.Phase == JobPhase.Running ? throw new InvalidOperationException("the repository failed") : inner.UpsertAsync(run, cancellationToken);
    }

    [Fact]
    public async Task A_VM_still_PROVISIONING_right_after_create_is_waited_for_not_failed()
    {
        var gcp = new FakeGcp().WithBootStatusPolls(3);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-a2", Spec("job-a2")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public async Task A_VM_that_never_finishes_booting_is_ended_at_the_boot_deadline()
    {
        // THEORY (unverified): stop on a non-running instance is rejected, so a VM that never reached RUNNING is deleted.
        var gcp = new FakeGcp().WithBootStatusPolls(1_000_000).WithStopRejectedUnlessRunning();
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-a3", Spec("job-a3")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.VmUnhealthy);
        (await gcp.FindByJobIdAsync("job-a3", CancellationToken.None)).ShouldBeEmpty("a VM that never booted has no disk worth keeping, and stop would be rejected");
    }

    // ---- B and C: nothing hangs ----

    [Fact]
    public async Task A_create_that_never_answers_ends_at_the_create_deadline()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var repo = new InMemoryRunRepository();

        var result = await WithinAsync(
            Runner(gcp, repo).RunAsync(TestInputs.Request("job-b", Spec("job-b")), CancellationToken.None),
            "a hung create was never cut at its deadline");

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.VmEndUnconfirmed, "the insert may still land, so the VM end cannot be confirmed");
        LastRow(repo, "job-b").Phase.ShouldBe(JobPhase.Failed);
        LastRow(repo, "job-b").ErrorDetail.ShouldNotBeNull().ShouldContain("network");
    }

    [Fact]
    public async Task A_call_that_ignores_its_cancellation_token_still_times_out()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = Runner(
            gcp,
            repo,
            (_, phase) =>
            {
                if (phase == JobPhase.Running)
                {
                    gcp.WithHungCalls(2, ignoreCancellation: true);
                }
            },
            TimeSpan.FromSeconds(30));

        var result = await WithinAsync(runner.RunAsync(TestInputs.Request("job-c", Spec("job-c")), CancellationToken.None), "a call that ignores its token hung the run");

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    // ---- D and E: resume boundary and the default wait ----

    [Fact]
    public async Task A_run_resumed_at_Provisioning_whose_worker_already_finished_never_creates_a_VM()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-d", Spec("job-d"));
        var bucket = FakeGcp.BucketName(Project);
        var track = FakeGcp.TrackBytes("seq");
        gcp.PutObject(bucket, "jobs/job-d/output/seq/seq.bedgraph", track);
        gcp.PutObject(bucket, "jobs/job-d/result.json", Encoding.UTF8.GetBytes(
            $$"""{"schema":1,"jobId":"job-d","status":"done","inputs":[{"id":"in1","status":"done","outputs":["output/seq/seq.bedgraph"],"files":[{"path":"output/seq/seq.bedgraph","sha256":"{{Convert.ToHexString(SHA256.HashData(track))}}","bytes":{{track.Length}}}],"notices":[],"stats":null,"error":null}],"error":null}"""));
        await repo.UpsertAsync(new RunRecord("job-d", JobPhase.Provisioning, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreateAttempts.ShouldBe(0, "a finished job must never create a second billed VM");
        File.ReadAllBytes(TrackPath(repo, "job-d")).ShouldBe(track);
    }

    [Fact]
    public async Task With_no_explicit_timeout_the_wait_ends_before_the_VMs_own_limit_and_records_result_timeout()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var spec = Spec("job-e", TimeSpan.FromSeconds(2));

        var result = await WithinAsync(
            Runner(gcp, repo, defaultWait: true).RunAsync(TestInputs.Request("job-e", spec), CancellationToken.None),
            "the default wait ignored the VM's maxRunDuration (the old limit was that plus 5 minutes)");

        result.FailureCode.ShouldBe(RunErrorCodes.ResultTimeout);
        (await VmStatusAsync(gcp, "job-e")).ShouldBe("STOPPED");
    }

    // ---- G: the output folder is proven before anything is created ----

    [Fact]
    public async Task An_output_folder_that_cannot_be_used_fails_before_anything_is_uploaded_or_created()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-g", Spec("job-g"));
        var blocker = Path.Combine(request.OutputFolder, "a-file-not-a-folder");
        File.WriteAllText(blocker, "x");
        request = request with { OutputFolder = blocker };

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.OutputFolderUnusable);
        gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldBeEmpty("nobody pays to learn their folder is unusable");
        gcp.CreateAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task The_run_folder_exists_and_is_recorded_before_the_VM_is_created_and_the_download_reuses_it()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        string? folderAtProvisioning = null;
        var runner = Runner(gcp, repo, (id, phase) =>
        {
            if (phase == JobPhase.Provisioning)
            {
                folderAtProvisioning = LastRow(repo, id).OutputDir;
            }
        });

        var result = await runner.RunAsync(TestInputs.Request("job-g2", Spec("job-g2")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        folderAtProvisioning.ShouldNotBeNull();
        Directory.Exists(folderAtProvisioning!).ShouldBeTrue();
        LastRow(repo, "job-g2").OutputDir.ShouldBe(folderAtProvisioning);
        File.Exists(TrackPath(repo, "job-g2")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_that_fails_before_any_result_leaves_no_empty_folder_behind()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.GarbageResult);
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-g3", Spec("job-g3"));

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        Directory.GetFileSystemEntries(request.OutputFolder).ShouldBeEmpty("an empty run folder from a failed run is clutter in the user's folder");
    }

    // ---- H: the run record never carries a file name ----

    [Fact]
    public async Task A_missing_input_is_recorded_without_its_file_name()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-h1", Spec("job-h1"), null, AfterTaskAction.Stop, null, "secret-sample.gb");
        File.Delete(request.Inputs[0].LocalPath);

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.InputMissing);
        LastRow(repo, "job-h1").ErrorDetail.ShouldNotBeNull().ShouldNotContain("secret-sample");
        result.FailureMessage.ShouldNotBeNull().ShouldNotContain("secret-sample");
    }

    [Theory]
    [InlineData(FakeWorkerMode.ChecksumMismatch)]
    [InlineData(FakeWorkerMode.UnsafePath)]
    public async Task A_download_failure_is_recorded_without_the_file_name_or_path(FakeWorkerMode mode)
    {
        var gcp = new FakeGcp().WithWorker(mode);
        var repo = new InMemoryRunRepository();
        var jobId = "job-h2-" + mode.ToString().ToLowerInvariant();
        var request = TestInputs.Request(jobId, Spec(jobId), null, AfterTaskAction.Stop, null, "secret-sample.gb");

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        var detail = LastRow(repo, jobId).ErrorDetail.ShouldNotBeNull();
        detail.ShouldNotContain("secret-sample");
        detail.ShouldNotContain("evil");
        detail.ShouldNotContain("bedgraph");
    }

    // ---- I: worker codes with their own user action ----

    [Theory]
    [InlineData("MODEL_OOM", "model_oom")]
    [InlineData("MODEL_NEEDS_HOPPER", "model_needs_hopper")]
    [InlineData("BATCH_LIMIT_EXCEEDED", "batch_limit_exceeded")]
    [InlineData("INPUT_INVALID", "input_invalid")]
    [InlineData("WORKER_VERSION_MISMATCH", "worker_version_mismatch")]
    [InlineData("MODEL_UNKNOWN", "worker_failed")]
    [InlineData("SOMETHING_NEW", "worker_failed")]
    public async Task A_failed_job_is_recorded_under_the_code_for_what_the_worker_reported(string workerCode, string expected)
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed).WithWorkerFailureCode(workerCode);
        var repo = new InMemoryRunRepository();
        var jobId = "job-i-" + expected.Replace('_', '-') + "-" + workerCode.ToLowerInvariant().Replace('_', '-');

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request(jobId, Spec(jobId)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(expected);
        LastRow(repo, jobId).ErrorDetail.ShouldNotBeNull().ShouldContain(workerCode, Case.Sensitive);
    }

    [Fact]
    public async Task An_input_failure_code_is_used_when_the_job_itself_names_none()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.AllInputsFailed).WithWorkerFailureCode("INPUT_INVALID");
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-i2", Spec("job-i2")), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.InputInvalid);
    }

    // ---- J: partial files of failed and cancelled inputs are kept ----

    [Fact]
    public async Task The_partial_file_of_a_failed_input_is_downloaded_and_the_run_reads_PartiallyCompleted()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.SecondInputFailed).WithPartialFilesOnFailedInputs();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-j1", Spec("job-j1"), null, AfterTaskAction.Stop, null, "a.gb", "b.gb");

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.PartiallyCompleted);
        var dir = LastRow(repo, "job-j1").OutputDir!;
        File.Exists(Path.Combine(dir, "a", "a.bedgraph")).ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(dir, "b", "b.partial.bedgraph")).ShouldBe(FakeGcp.TrackBytes("b-partial"));
    }

    [Fact]
    public async Task A_run_where_every_input_failed_still_keeps_the_partial_files_and_says_so()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.AllInputsFailed).WithPartialFilesOnFailedInputs();
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-j2", Spec("job-j2")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        var row = LastRow(repo, "job-j2");
        row.OutputDir.ShouldNotBeNull();
        File.Exists(Path.Combine(row.OutputDir!, "seq", "seq.partial.bedgraph")).ShouldBeTrue("partial results are always kept (docs/job_contract.md section 6)");
        row.ErrorDetail.ShouldNotBeNull().ToLowerInvariant().ShouldContain("partial");
    }

    // ---- K and L: the VM end honours the user's choice ----

    [Fact]
    public async Task A_failed_run_deletes_the_VM_when_the_keep_alive_choice_was_delete_afterwards()
    {
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.WholeJobFailed);
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-k", Spec("job-k"), null, AfterTaskAction.KeepAlive);
        request = request with { Options = request.Options with { AfterKeepAlive = AfterKeepAliveAction.Delete } };

        var result = await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        (await gcp.FindByJobIdAsync("job-k", CancellationToken.None)).ShouldBeEmpty("Delete was the user's choice for after the window");
    }

    [Fact]
    public async Task A_boot_failure_with_Delete_chosen_is_deleted_by_the_runner_not_left_stopped_by_the_worker()
    {
        // The fake's worker only STOPS the VM for an image pull failure, so a missing VM afterwards is the runner's own end call.
        var gcp = new FakeGcp().WithWorker(FakeWorkerMode.ImagePullFailed);
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-l", Spec("job-l"), null, AfterTaskAction.Delete), CancellationToken.None);

        result.FailureCode.ShouldBe(RunErrorCodes.ImagePullFailed);
        (await gcp.FindByJobIdAsync("job-l", CancellationToken.None)).ShouldBeEmpty();
    }
}
