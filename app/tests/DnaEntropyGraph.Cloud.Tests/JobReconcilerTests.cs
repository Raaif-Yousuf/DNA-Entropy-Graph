using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.Runs;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #59: on launch every non-terminal run is reattached from what the cloud and the bucket say, through the one resume
/// path the runner already has. Each test seeds a run row the way a killed app leaves it (phase, VM, bucket objects) and asserts
/// what a user would find afterwards: the recorded phase, the VMs that exist, the files on disk.
/// </summary>
public class JobReconcilerTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";
    private static readonly string Image = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:" + new string('a', 64);
    private static readonly DateTimeOffset Launch = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class RecordingLog : IDiagnosticsLog
    {
        public List<(string Source, string? JobId, string ErrorClass)> Entries { get; } = [];

        private readonly List<(Func<(string Source, string? JobId, string ErrorClass), bool> Match, TaskCompletionSource Signal)> _waiters = [];

        public void Warning(string source, string? jobId, string errorClass)
        {
            lock (Entries)
            {
                Entries.Add((source, jobId, errorClass));
                _waiters.RemoveAll(w =>
                {
                    if (!w.Match((source, jobId, errorClass)))
                    {
                        return false;
                    }

                    w.Signal.TrySetResult();
                    return true;
                });
            }
        }

        /// <summary>Completes once an entry matching <paramref name="match"/> has been logged (already complete when one was): a signal instead of a poll of <see cref="Entries"/> on a real clock.</summary>
        public Task WhenLoggedAsync(Func<(string Source, string? JobId, string ErrorClass), bool> match)
        {
            lock (Entries)
            {
                if (Entries.Any(match))
                {
                    return Task.CompletedTask;
                }

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((match, signal));
                return signal.Task;
            }
        }
    }

    internal sealed class Env
    {
        public Env(FakeGcp? gcp = null, WorkerImageResolution? image = null)
        {
            Gcp = gcp ?? new FakeGcp();
            Repo = new InMemoryRunRepository();
            AppData = Path.Combine(Path.GetTempPath(), "deg-reconciler-" + Guid.NewGuid().ToString("N"));
            Inputs = new LocalRunInputStore(AppData);
            Images = Substitute.For<IWorkerImageProvider>();
            Images.Resolve(Arg.Any<string>(), Arg.Any<bool>()).Returns(image ?? new WorkerImageResolution(WorkerImageStatus.Available, Image));
        }

        public FakeGcp Gcp { get; }

        public TimeSpan ResultTimeout { get; set; } = TimeSpan.FromMilliseconds(80);

        public RecordingLog Log { get; } = new();

        public InMemoryRunRepository Repo { get; }

        public string AppData { get; }

        public LocalRunInputStore Inputs { get; }

        public IWorkerImageProvider Images { get; }

        public List<(string JobId, JobPhase Phase)> Notified { get; } = [];

        public ActiveRuns Active { get; } = new();

        public MemorySettingsStore Settings { get; } = new() { Values = { [InstallationId.SettingsKey] = "install-1" } };

        /// <summary>The runner's clocks (#567): its call, result and lifecycle deadlines run on test time, so a loaded machine cannot cut a call. No park pump: a test that scripts a hung call here wants it parked until its own cancel.</summary>
        public RunnerClocks Clocks { get; } = new();

        public CloudJobRunner Runner
        {
            get
            {
                return new(Gcp, Gcp, Gcp, Gcp, Gcp, Repo, (id, phase) => Notified.Add((id, phase)))
                {
                    ResultPollInterval = TimeSpan.FromMilliseconds(1),
                    ResultTimeout = ResultTimeout,
                    CallTimeout = TimeSpan.FromMilliseconds(200),
                    LifecycleTimeout = TimeSpan.FromMilliseconds(80),
                    LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
                    TimeProvider = Clocks.Run,
                    PollDelay = Clocks.SleepAsync,
                };
            }
        }

        public JobReconciler Reconciler(TimeProvider? clock = null, TimeSpan? lookupTimeout = null, TimeSpan? mutationTimeout = null, IDiagnosticsLog? log = null) => new(
            Runner,
            Gcp,
            Gcp,
            Repo,
            Inputs,
            Images,
            Active,
            Settings,
            (id, phase) => Notified.Add((id, phase)),
            () => Path.Combine(AppData, "downloads"),
            clock ?? new FixedClock(Launch),
            lookupTimeout,
            mutationTimeout,
            log ?? Log);

        public RunRecord Row(string jobId) => Repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

        /// <summary>A killed app's leftovers for an NCBI accession run (issue #684): a row at <paramref name="phase"/> whose options name the accession, no copy of any input, no VM.</summary>
        public async Task SeedAccessionAsync(string jobId, JobPhase phase, string accession, string? range = null, bool uploaded = false, string? annotationPath = null)
        {
            var options = new RunOptions
            {
                ModelId = "evo2_7b",
                RunTarget = "Cloud",
                Accession = accession,
                AccessionRange = range,
                AnnotationPath = annotationPath,
                OutputFolder = TestInputs.OutputParent(jobId),
            };
            var vmName = "deg-" + jobId;
            if (uploaded)
            {
                var request = CloudJobRequestFactory.Create(options, jobId, Project, "install-1", "0.1.0", Image, [StagedInput.ForAccession(accession, range)], options.OutputFolder!);
                await Runner.UploadInputsAsync(request, CancellationToken.None);
                vmName = request.Spec.VmName;
            }

            await Repo.UpsertAsync(
                new RunRecord(
                    jobId,
                    phase,
                    Launch.AddMinutes(-5),
                    Target: "cloud",
                    OptionsJson: RunOptionsJson.Serialize(options),
                    ProjectId: Project,
                    Bucket: uploaded ? FakeGcp.BucketName(Project) : null,
                    VmName: vmName,
                    AppVersion: "0.1.0",
                    InstallationId: "install-1"),
                CancellationToken.None);
        }

        /// <summary>
        /// The state a killed app leaves behind: the run row at <paramref name="phase"/>, the app's own copy of the input,
        /// optionally the uploaded manifest and a VM (whose simulated worker has already acted per the fake's mode).
        /// </summary>
        public async Task<CloudJobRequest> SeedAsync(
            string jobId,
            JobPhase phase,
            bool uploaded = true,
            bool vm = false,
            bool keepOriginal = true,
            AfterTaskAction after = AfterTaskAction.Stop,
            Func<RunRecord, RunRecord>? tweak = null,
            AfterKeepAliveAction afterKeepAlive = AfterKeepAliveAction.Stop,
            int keepAliveMinutes = 30,
            bool paired = false,
            bool keepAnnotationOriginal = true,
            bool keepAnnotationCopy = true,
            string annotationContent = TestInputs.PairedGff3)
        {
            var original = paired ? TestInputs.Stage(jobId, "seq.fasta", TestInputs.PairedFasta) : TestInputs.Stage(jobId);
            var copy = await Inputs.StageAsync(jobId, original.LocalPath, CancellationToken.None);
            string? annotationOriginal = null;
            StagedInput? annotationCopy = null;
            if (paired)
            {
                // Issue #720: a run paired with a GFF3 (#686) kept the app's own copy under runs/<job>/annotation.
                annotationOriginal = TestInputs.Stage(jobId, "genes.gff3", annotationContent).LocalPath;
                annotationCopy = await Inputs.StageAnnotationAsync(jobId, annotationOriginal, CancellationToken.None);
            }
            if (!keepOriginal)
            {
                File.Delete(original.LocalPath);
            }

            var options = new RunOptions
            {
                ModelId = "evo2_7b",
                RunTarget = "Cloud",
                AfterTask = after,
                AfterKeepAlive = afterKeepAlive,
                KeepAliveMinutes = keepAliveMinutes,
                InputPath = original.LocalPath,
                AnnotationPath = annotationOriginal,
                OutputFolder = TestInputs.OutputParent(jobId),
            };
            var request = CloudJobRequestFactory.Create(options, jobId, Project, "install-1", "0.1.0", Image, annotationCopy is null ? [copy] : [copy, annotationCopy], options.OutputFolder!);
            if (uploaded)
            {
                await Runner.UploadInputsAsync(request, CancellationToken.None);
            }

            // After the upload, so a run whose VM already holds the pair can also have lost its local GFF3 (issue #720).
            if (paired && !keepAnnotationOriginal)
            {
                File.Delete(annotationOriginal!);
            }

            if (paired && !keepAnnotationCopy)
            {
                Directory.Delete(Path.Combine(AppData, "runs", jobId, "annotation"), recursive: true);
            }

            if (vm)
            {
                await Gcp.CreateVmAsync(request.Spec, Zone, CancellationToken.None);
            }

            var row = new RunRecord(
                jobId,
                phase,
                Launch.AddMinutes(-5),
                Target: "cloud",
                OptionsJson: RunOptionsJson.Serialize(options),
                ProjectId: Project,
                Bucket: uploaded ? FakeGcp.BucketName(Project) : null,
                VmName: request.Spec.VmName,
                AppVersion: "0.1.0",
                InstallationId: "install-1");
            await Repo.UpsertAsync(tweak is null ? row : tweak(row), CancellationToken.None);
            return request;
        }
    }

    private static async Task<ReattachOutcome> OneAsync(JobReconciler reconciler, string jobId)
    {
        var outcomes = await reconciler.ReattachAsync(CancellationToken.None);
        return outcomes.Single(o => o.JobId == jobId);
    }

    // ---- the decision table: a run past Provisioning is judged by the VM and result.json ----

    [Fact]
    public async Task A_run_killed_while_Running_with_its_VM_up_and_the_result_written_completes_without_a_second_VM()
    {
        var env = new Env();
        var request = await env.SeedAsync("job-a", JobPhase.Running, vm: true);
        var createsBefore = env.Gcp.CreateAttempts;

        var outcome = await OneAsync(env.Reconciler(), "job-a");

        outcome.Action.ShouldBe(ReattachAction.Resumed);
        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Row("job-a").Phase.ShouldBe(JobPhase.Completed);
        env.Gcp.CreateAttempts.ShouldBe(createsBefore, "reattaching must never create a second billed VM");
        Directory.EnumerateFiles(env.Row("job-a").OutputDir!, "*", SearchOption.AllDirectories).ShouldNotBeEmpty("the track must reach the output folder");
        env.Notified.ShouldContain(("job-a", JobPhase.Completed), "the Runs page is told through the same callback a live run uses");
        request.JobId.ShouldBe("job-a");
    }

    [Fact]
    public async Task A_run_killed_while_Downloading_whose_VM_is_long_gone_still_completes()
    {
        // Raw-Compute shape ON PURPOSE (#938 round 5 audit): the terminator tolerates a 404 on delete; the real gateway swallows it, so this proves the terminator's own tolerance only.
        var env = new Env(new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete).WithDeleteOfMissingVmNotFound());
        var request = await env.SeedAsync("job-d", JobPhase.Downloading, vm: true);
        await env.Gcp.DeleteVmAsync(request.Spec.VmName, Zone, CancellationToken.None);
        var createsBefore = env.Gcp.CreateAttempts;

        var outcome = await OneAsync(env.Reconciler(), "job-d");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Gcp.CreateAttempts.ShouldBe(createsBefore);
    }

    [Fact]
    public async Task A_run_killed_while_Running_whose_VM_is_gone_and_left_no_result_fails_as_a_lost_VM_and_never_provisions_again()
    {
        var env = new Env();
        await env.SeedAsync("job-m", JobPhase.Running, uploaded: true, vm: false);

        var outcome = await OneAsync(env.Reconciler(), "job-m");

        outcome.Action.ShouldBe(ReattachAction.FailedVmMissing);
        env.Row("job-m").Phase.ShouldBe(JobPhase.Failed);
        env.Row("job-m").ErrorCode.ShouldBe(RunErrorCodes.VmUnhealthy);
        env.Row("job-m").ErrorDetail.ShouldNotBeNullOrWhiteSpace();
        env.Gcp.CreateAttempts.ShouldBe(0, "a lost VM is not silently replaced: the user chooses to run again");
    }

    // Issue #679 (b), reviewed: a gone VM is run_time_limit only on the worker's OWN evidence of how long the VM lived (status.json startedAt to
    // updatedAt, one clock), never on the row's start time, which counts the hours the app was closed after the VM had already died.
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }.MaxRunDurationMinutes) + CloudJobRequestFactory.VmGrace;

    private static void PutStatus(Env env, string jobId, string json)
        => env.Gcp.PutObject(FakeGcp.BucketName(Project), WorkerManifestBuilder.JobPrefix(jobId) + "status.json", System.Text.Encoding.UTF8.GetBytes(json));

    private static string Heartbeat(TimeSpan lived)
    {
        var began = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        return "{\"stage\":\"running\",\"heartbeatSeq\":9,\"startedAt\":\"" + began.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
            + "\",\"stageStartedAt\":\"" + began.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
            + "\",\"updatedAt\":\"" + (began + lived).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) + "\",\"worker\":{\"version\":\"0.1.0\"}}";
    }

    [Fact]
    public async Task A_VM_that_died_early_while_the_app_was_closed_is_not_run_time_limit_even_after_20_hours()
    {
        var env = new Env();
        await env.SeedAsync("job-l", JobPhase.Running, uploaded: true, vm: false, tweak: row => row with { StartedAt = Launch.AddHours(-20) });
        PutStatus(env, "job-l", Heartbeat(TimeSpan.FromMinutes(3)));

        var outcome = await OneAsync(env.Reconciler(), "job-l");

        outcome.Action.ShouldBe(ReattachAction.FailedVmMissing);
        env.Row("job-l").ErrorCode.ShouldBe(RunErrorCodes.VmUnhealthy, "its last heartbeat says it lived 3 minutes: it did not reach its limit");
    }

    [Theory]
    [InlineData(-2, RunErrorCodes.RunTimeLimit)]
    [InlineData(0, RunErrorCodes.RunTimeLimit)]
    [InlineData(-9, RunErrorCodes.RunTimeLimit)]
    [InlineData(-11, RunErrorCodes.VmUnhealthy)]
    [InlineData(-60, RunErrorCodes.VmUnhealthy)]
    public async Task A_gone_VM_with_a_heartbeat_near_its_limit_is_run_time_limit_and_one_far_from_it_is_not(int minutesFromLimit, string expected)
    {
        var env = new Env();
        await env.SeedAsync("job-h", JobPhase.Running, uploaded: true, vm: false, tweak: row => row with { StartedAt = Launch.AddHours(-20) });
        PutStatus(env, "job-h", Heartbeat(RunLimit + TimeSpan.FromMinutes(minutesFromLimit)));

        await OneAsync(env.Reconciler(), "job-h");

        env.Row("job-h").ErrorCode.ShouldBe(expected);
    }

    [Fact]
    public async Task A_gone_VM_with_a_worker_error_in_status_json_records_that_error_not_a_limit()
    {
        var env = new Env();
        await env.SeedAsync("job-w", JobPhase.Running, uploaded: true, vm: false, tweak: row => row with { StartedAt = Launch.AddHours(-20) });
        PutStatus(env, "job-w", "{\"stage\":\"failed\",\"error\":{\"code\":\"WORKER_CRASH\"},\"updatedAt\":\"2026-10-02T09:01:00Z\"}");

        await OneAsync(env.Reconciler(), "job-w");

        env.Row("job-w").ErrorCode.ShouldBe(RunErrorCodes.WorkerCrashed, "the worker said why; the reconciler must read it as the live path does");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    public async Task A_gone_VM_with_no_usable_status_json_is_vm_unhealthy_whatever_the_rows_age(string? status)
    {
        var env = new Env();
        await env.SeedAsync("job-n0", JobPhase.Running, uploaded: true, vm: false, tweak: row => row with { StartedAt = Launch.AddHours(-20) });
        if (status is not null)
        {
            PutStatus(env, "job-n0", status);
        }

        await OneAsync(env.Reconciler(), "job-n0");

        env.Row("job-n0").ErrorCode.ShouldBe(RunErrorCodes.VmUnhealthy, "with no evidence of how long the VM lived, the age of the row proves nothing");
    }
    [Fact]
    public async Task A_run_whose_VM_stopped_without_a_result_fails_and_the_VM_is_ended()
    {
        var env = new Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        var request = await env.SeedAsync("job-s", JobPhase.Running, vm: true);
        await env.Gcp.StopVmAsync(request.Spec.VmName, Zone, CancellationToken.None);

        var outcome = await OneAsync(env.Reconciler(), "job-s");

        outcome.FinalPhase.ShouldBe(JobPhase.Failed);
        env.Row("job-s").ErrorCode.ShouldBe(RunErrorCodes.VmUnhealthy);
        (await env.Gcp.FindByJobIdAsync("job-s", CancellationToken.None))
            .ShouldAllBe(vm => vm.Status == "STOPPED" || vm.Status == "TERMINATED", "a stopped VM stays ended, it is never restarted");
    }

    [Fact]
    public async Task A_run_whose_VM_is_still_up_with_no_result_resumes_polling_for_the_result()
    {
        var env = new Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-p", JobPhase.Running, vm: true);

        var outcome = await OneAsync(env.Reconciler(), "job-p");

        outcome.Action.ShouldBe(ReattachAction.Resumed);
        env.Row("job-p").ErrorCode.ShouldBe(RunErrorCodes.ResultTimeout, "the only way to this code is to have waited on the VM for result.json");
    }

    [Fact]
    public async Task A_run_killed_at_Provisioning_before_any_VM_existed_provisions_it_now()
    {
        var env = new Env();
        await env.SeedAsync("job-v", JobPhase.Provisioning, uploaded: true, vm: false);

        var outcome = await OneAsync(env.Reconciler(), "job-v");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Gcp.CreateAttempts.ShouldBe(1);
        (await env.Gcp.FindByJobIdAsync("job-v", CancellationToken.None)).Count.ShouldBe(1);
    }

    // ---- an NCBI accession run (issue #684) has no file: nothing to find, nothing to upload ----

    [Theory]
    [InlineData(JobPhase.Validating)]
    [InlineData(JobPhase.Provisioning)]
    public async Task An_accession_run_killed_before_a_VM_existed_resumes_with_no_upload_and_its_manifest_names_the_accession(JobPhase phase)
    {
        var env = new Env();
        await env.SeedAccessionAsync("job-n", phase, "NC_045512.2", "100-500", uploaded: phase == JobPhase.Provisioning);

        var outcome = await OneAsync(env.Reconciler(), "job-n");

        outcome.Action.ShouldBe(ReattachAction.Resumed, outcome.ErrorCode);
        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Gcp.CreateAttempts.ShouldBe(1);
        var keys = env.Gcp.ObjectKeys(FakeGcp.BucketName(Project));
        keys.ShouldNotContain(key => key.Contains("/input/", StringComparison.Ordinal), "an accession is fetched by the worker, so no input is uploaded");
        var manifest = System.Text.Json.JsonDocument.Parse(env.Gcp.GetObjectBytes(FakeGcp.BucketName(Project), "jobs/job-n/manifest.json")).RootElement;
        var input = manifest.GetProperty("inputs")[0];
        input.GetProperty("accession").GetString().ShouldBe("NC_045512.2");
        input.GetProperty("accessionRange").GetString().ShouldBe("100-500");
        input.TryGetProperty("path", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("not an accession", null)]
    [InlineData("NC_045512.2", "500-100")]
    public async Task An_accession_run_whose_recorded_accession_no_longer_checks_out_fails_as_invalid_and_creates_nothing(string accession, string? range)
    {
        // The row is the app's own file on disk; a damaged or hand-edited one must not reach the cloud (Hard Rule 2).
        var env = new Env();
        await env.SeedAccessionAsync("job-x", JobPhase.Provisioning, accession, range);

        var outcome = await OneAsync(env.Reconciler(), "job-x");

        outcome.Action.ShouldBe(ReattachAction.FailedUnrecoverable);
        env.Row("job-x").ErrorCode.ShouldBe(RunErrorCodes.InputAccessionInvalid);
        env.Gcp.CreateAttempts.ShouldBe(0);
    }

    // ---- a run killed before a VM could exist restarts from the app's own copy of the input ----

    [Theory]
    [InlineData(JobPhase.Draft)]
    [InlineData(JobPhase.Validating)]
    [InlineData(JobPhase.Uploading)]
    public async Task A_run_killed_before_Provisioning_restarts_from_the_apps_copy_of_the_input_even_when_the_original_is_gone(JobPhase phase)
    {
        var env = new Env();
        await env.SeedAsync("job-u", phase, uploaded: false, keepOriginal: false);

        var outcome = await OneAsync(env.Reconciler(), "job-u");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldContain("jobs/job-u/manifest.json");
        env.Gcp.CreateAttempts.ShouldBe(1);
    }

    // ---- a run paired with a GFF3 (issue #686) keeps the pairing across a reattach (issue #720) ----

    private static System.Text.Json.JsonElement FirstManifestInput(Env env, string jobId)
        => System.Text.Json.JsonDocument.Parse(env.Gcp.GetObjectBytes(FakeGcp.BucketName(Project), "jobs/" + jobId + "/manifest.json")).RootElement.GetProperty("inputs")[0];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_paired_run_killed_before_a_VM_existed_re_uploads_its_GFF3_from_the_apps_copy_whether_or_not_the_original_remains_and_the_manifest_names_it(bool originalStillThere)
    {
        var env = new Env();
        await env.SeedAsync("job-g", JobPhase.Validating, uploaded: false, keepOriginal: false, paired: true, keepAnnotationOriginal: originalStillThere);

        var outcome = await OneAsync(env.Reconciler(), "job-g");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        var keys = env.Gcp.ObjectKeys(FakeGcp.BucketName(Project));
        keys.ShouldContain("jobs/job-g/input/genes.gff3", "the paired annotation must be in the bucket, or the worker finds genes itself");
        FirstManifestInput(env, "job-g").GetProperty("annotation").GetString().ShouldBe("input/genes.gff3");
    }

    [Fact]
    public async Task A_paired_run_whose_GFF3_copy_is_gone_is_restaged_from_the_original_not_dropped()
    {
        var env = new Env();
        await env.SeedAsync("job-r", JobPhase.Validating, uploaded: false, keepOriginal: false, paired: true, keepAnnotationCopy: false);

        var outcome = await OneAsync(env.Reconciler(), "job-r");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        FirstManifestInput(env, "job-r").GetProperty("annotation").GetString().ShouldBe("input/genes.gff3");
    }

    [Fact]
    public async Task A_paired_run_whose_GFF3_is_gone_everywhere_fails_naming_the_missing_input_and_creates_nothing()
    {
        var env = new Env();
        await env.SeedAsync("job-m", JobPhase.Validating, uploaded: false, keepOriginal: false, paired: true, keepAnnotationOriginal: false, keepAnnotationCopy: false);

        var outcome = await OneAsync(env.Reconciler(), "job-m");

        outcome.Action.ShouldBe(ReattachAction.FailedUnrecoverable);
        env.Row("job-m").ErrorCode.ShouldBe(RunErrorCodes.InputMissing);
        env.Gcp.CreateAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData(JobPhase.Running, "gone")]
    [InlineData(JobPhase.Running, "changed")]
    [InlineData(JobPhase.Provisioning, "gone")]
    [InlineData(JobPhase.Provisioning, "changed")]
    public async Task A_paired_run_whose_VM_already_exists_is_resumed_even_when_its_GFF3_is_gone_or_changed(JobPhase phase, string state)
    {
        // The VM already holds the uploaded pair: neither the missing-annotation failure nor the pair re-check applies once a VM may exist.
        var env = new Env();
        await env.SeedAsync(
            "job-w",
            phase,
            uploaded: true,
            vm: true,
            paired: true,
            keepAnnotationOriginal: false,
            keepAnnotationCopy: state == "changed",
            annotationContent: state == "changed" ? TestInputs.MismatchedGff3 : TestInputs.PairedGff3);

        var outcome = await OneAsync(env.Reconciler(), "job-w");

        outcome.Action.ShouldBe(ReattachAction.Resumed);
        env.Row("job-w").ErrorCode.ShouldNotBe(RunErrorCodes.InputMissing);
        env.Row("job-w").ErrorCode.ShouldNotBe(RunErrorCodes.InputInvalid);
    }

    [Fact]
    public async Task A_paired_run_whose_original_GFF3_changed_to_one_that_no_longer_fits_its_FASTA_fails_as_invalid_and_creates_nothing()
    {
        var env = new Env();
        await env.SeedAsync("job-x", JobPhase.Validating, uploaded: false, keepOriginal: false, paired: true, keepAnnotationCopy: false, annotationContent: TestInputs.MismatchedGff3);

        var outcome = await OneAsync(env.Reconciler(), "job-x");

        outcome.Action.ShouldBe(ReattachAction.FailedUnrecoverable);
        env.Row("job-x").ErrorCode.ShouldBe(RunErrorCodes.InputInvalid);
        env.Gcp.CreateAttempts.ShouldBe(0);
        env.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldNotContain(k => k.EndsWith(".gff3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_accession_run_with_a_stale_AnnotationPath_uploads_no_annotation_and_its_manifest_has_none()
    {
        var env = new Env();
        var gff = TestInputs.Stage("job-ag", "genes.gff3", TestInputs.PairedGff3).LocalPath;
        await env.SeedAccessionAsync("job-ag", JobPhase.Validating, "NC_045512.2", annotationPath: gff);

        var outcome = await OneAsync(env.Reconciler(), "job-ag");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldNotContain(k => k.EndsWith(".gff3", StringComparison.Ordinal));
        FirstManifestInput(env, "job-ag").TryGetProperty("annotation", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_that_was_not_paired_uploads_no_annotation_and_its_manifest_has_none()
    {
        var env = new Env();
        await env.SeedAsync("job-p", JobPhase.Validating, uploaded: false, keepOriginal: false);

        var outcome = await OneAsync(env.Reconciler(), "job-p");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldNotContain(k => k.EndsWith(".gff3", StringComparison.Ordinal));
        FirstManifestInput(env, "job-p").TryGetProperty("annotation", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_killed_before_Provisioning_with_no_copy_of_its_input_fails_naming_the_missing_input()
    {
        var env = new Env();
        await env.SeedAsync("job-i", JobPhase.Validating, uploaded: false, keepOriginal: false);
        Directory.Delete(Path.Combine(env.AppData, "runs", "job-i"), recursive: true);

        var outcome = await OneAsync(env.Reconciler(), "job-i");

        outcome.Action.ShouldBe(ReattachAction.FailedUnrecoverable);
        env.Row("job-i").Phase.ShouldBe(JobPhase.Failed);
        env.Row("job-i").ErrorCode.ShouldBe(RunErrorCodes.InputMissing);
        env.Gcp.CreateAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_killed_before_Provisioning_with_no_worker_image_for_this_version_fails_naming_the_image_and_creates_nothing()
    {
        var env = new Env(image: new WorkerImageResolution(WorkerImageStatus.NoneShipped, null));
        await env.SeedAsync("job-w", JobPhase.Uploading, uploaded: false);

        var outcome = await OneAsync(env.Reconciler(), "job-w");

        env.Row("job-w").ErrorCode.ShouldBe(RunErrorCodes.WorkerImageUnavailable);
        outcome.Action.ShouldBe(ReattachAction.FailedUnrecoverable);
        env.Gcp.CreateAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_past_Provisioning_does_not_need_a_worker_image_to_be_reattached()
    {
        var env = new Env(image: new WorkerImageResolution(WorkerImageStatus.NoneShipped, null));
        await env.SeedAsync("job-n", JobPhase.Running, vm: true);

        var outcome = await OneAsync(env.Reconciler(), "job-n");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
    }

    [Theory]
    [InlineData("no project")]
    [InlineData("bad options")]
    public async Task A_row_that_cannot_be_rebuilt_into_a_request_fails_with_a_recorded_code_instead_of_staying_non_terminal(string flaw)
    {
        var env = new Env();
        await env.SeedAsync("job-r", JobPhase.Validating, uploaded: false, tweak: row => flaw == "no project" ? row with { ProjectId = null } : row with { OptionsJson = "{}" });

        var outcome = await OneAsync(env.Reconciler(), "job-r");

        outcome.Action.ShouldBe(ReattachAction.FailedUnrecoverable);
        env.Row("job-r").Phase.ShouldBe(JobPhase.Failed);
        env.Row("job-r").ErrorCode.ShouldBe(flaw == "no project" ? RunErrorCodes.NoProject : RunErrorCodes.Other);
    }

    // ---- cancelling, terminal rows, rows of this launch, local rows ----

    [Fact]
    public async Task A_run_killed_while_Cancelling_finishes_the_cancel_and_the_VM_is_deleted()
    {
        var env = new Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-c", JobPhase.Cancelling, vm: true);

        var outcome = await OneAsync(env.Reconciler(), "job-c");

        outcome.Action.ShouldBe(ReattachAction.CancelFinished);
        env.Row("job-c").Phase.ShouldBe(JobPhase.Cancelled);
        (await env.Gcp.FindByJobIdAsync("job-c", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Terminal_rows_rows_made_after_launch_and_local_rows_are_left_alone()
    {
        var env = new Env();
        await env.SeedAsync("job-done", JobPhase.Completed, uploaded: false);
        await env.SeedAsync("job-fresh", JobPhase.Validating, uploaded: false, tweak: row => row with { CreatedUtc = Launch.AddSeconds(1) });
        await env.SeedAsync("job-local", JobPhase.Running, uploaded: false, tweak: row => row with { Target = "local" });
        var before = env.Repo.AllRecordedInOrder.Count;

        var outcomes = await env.Reconciler().ReattachAsync(CancellationToken.None);

        outcomes.Select(o => o.JobId).ShouldBe(["job-local"]);
        outcomes.Single().Action.ShouldBe(ReattachAction.Skipped);
        env.Repo.AllRecordedInOrder.Count.ShouldBe(before, "nothing is written for a row the reconciler does not own");
        env.Gcp.CreateAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task Several_non_terminal_runs_are_all_reattached()
    {
        var env = new Env();
        await env.SeedAsync("job-1", JobPhase.Running, vm: true);
        await env.SeedAsync("job-2", JobPhase.Uploading, uploaded: false);
        await env.SeedAsync("job-3", JobPhase.Provisioning, uploaded: true);

        var outcomes = await env.Reconciler().ReattachAsync(CancellationToken.None);

        outcomes.Count.ShouldBe(3, "vacuity: the loop must see every seeded row");
        outcomes.ShouldAllBe(o => o.FinalPhase == JobPhase.Completed);
    }

    [Fact]
    public async Task Reattaching_twice_does_not_run_a_finished_run_again()
    {
        var env = new Env();
        await env.SeedAsync("job-t", JobPhase.Running, vm: true);
        var reconciler = env.Reconciler();
        await reconciler.ReattachAsync(CancellationToken.None);
        var rowsAfterFirst = env.Repo.AllRecordedInOrder.Count;

        var second = await reconciler.ReattachAsync(CancellationToken.None);

        second.ShouldBeEmpty();
        env.Repo.AllRecordedInOrder.Count.ShouldBe(rowsAfterFirst);
    }

    // ---- no cloud connected, or offline: never a run falsely failed ----

    [Fact]
    public async Task With_no_cloud_connected_a_run_past_Provisioning_is_left_as_it_is_not_failed()
    {
        var env = new Env(new FakeGcp().WithCloudNotConnected());
        await env.SeedAsync("job-x", JobPhase.Running, uploaded: false, vm: false);

        var outcome = await OneAsync(env.Reconciler(), "job-x");

        outcome.Action.ShouldBe(ReattachAction.Deferred);
        env.Row("job-x").Phase.ShouldBe(JobPhase.Running);
        env.Row("job-x").ErrorCode.ShouldBeNull("a missing connection says nothing about the run; the next launch looks again");
    }

    [Fact]
    public async Task With_no_cloud_connected_a_run_killed_before_Provisioning_fails_with_the_not_connected_code()
    {
        var env = new Env(new FakeGcp().WithCloudNotConnected());
        await env.SeedAsync("job-y", JobPhase.Validating, uploaded: false);

        var outcome = await OneAsync(env.Reconciler(), "job-y");

        env.Row("job-y").ErrorCode.ShouldBe(RunErrorCodes.CloudNotConnected, "the same code a live run gets, naming one action");
        outcome.FinalPhase.ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task A_network_failure_looking_for_the_VM_defers_the_run_and_writes_nothing()
    {
        var env = new Env();
        await env.SeedAsync("job-o", JobPhase.Running, vm: true);
        env.Gcp.WithFindByJobIdFailure(new CloudError(null, null, "could not reach the service"), 1);
        var before = env.Repo.AllRecordedInOrder.Count;

        var outcome = await OneAsync(env.Reconciler(), "job-o");

        outcome.Action.ShouldBe(ReattachAction.Deferred);
        env.Repo.AllRecordedInOrder.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_stuck_listing_looking_for_a_live_VM_defers_the_run_and_leaves_the_row_and_the_VM_alone()
    {
        // Issue #933 DECISION: LIST_PAGINATION_STUCK is kind Network, so the reconciler defers (no answer). Kind Other would hand the run to the
        // runner, whose own lookup fails the same way, ends the row Failed and cannot find the VM to end: a live, billing VM orphaned.
        var env = new Env();
        await env.SeedAsync("job-st", JobPhase.Running, vm: true);
        var stuck = Should.Throw<CloudOperationException>(() =>
        {
            var guard = new DnaEntropyGraph.Cloud.Rest.PageTokenGuard();
            guard.Advance("same");
            guard.Advance("same");
        });
        var compute = Substitute.For<IComputeGateway>();
        compute.FindByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VmDescriptor>>>(_ => throw stuck);
        var reconciler = new JobReconciler(env.Runner, compute, env.Gcp, env.Repo, env.Inputs, env.Images, env.Active, env.Settings, (id, phase) => env.Notified.Add((id, phase)), () => Path.Combine(env.AppData, "downloads"), new FixedClock(Launch), null, null, env.Log);
        var before = env.Repo.AllRecordedInOrder.Count;

        var outcome = await OneAsync(reconciler, "job-st");

        stuck.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        outcome.Action.ShouldBe(ReattachAction.Deferred);
        reconciler.HasDeferred.ShouldBeTrue("the reconnect probe asks again");
        env.Repo.AllRecordedInOrder.Count.ShouldBe(before, "a deferred row writes nothing");
        env.Row("job-st").Phase.ShouldBe(JobPhase.Running);
        (await env.Gcp.FindByJobIdAsync("job-st", CancellationToken.None)).Count.ShouldBe(1, "the VM is not ended");
    }

    // ---- cold review: a failed look is not evidence, and a look never creates anything ----

    [Fact]
    public async Task A_Provisioning_row_whose_first_look_is_refused_still_needs_its_worker_image_and_creates_no_VM()
    {
        var env = new Env(image: new WorkerImageResolution(WorkerImageStatus.NoneShipped, null));
        await env.SeedAsync("job-e", JobPhase.Provisioning, uploaded: true, vm: false);
        env.Gcp.WithFindByJobIdFailure(new CloudError("PERMISSION_DENIED", 403, "permission denied"), 1);

        var outcome = await OneAsync(env.Reconciler(), "job-e");

        outcome.Action.ShouldBe(ReattachAction.FailedUnrecoverable);
        env.Row("job-e").ErrorCode.ShouldBe(RunErrorCodes.WorkerImageUnavailable);
        env.Gcp.CreateAttempts.ShouldBe(0, "a VM created with no startup script would bill until maxRunDuration");
        (await env.Gcp.FindByJobIdAsync("job-e", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_with_no_recorded_bucket_is_judged_without_creating_a_bucket()
    {
        var env = new Env();
        await env.SeedAsync("job-b", JobPhase.Running, uploaded: false, vm: false);
        env.Row("job-b").Bucket.ShouldBeNull("precondition");

        var outcome = await OneAsync(env.Reconciler(), "job-b");

        outcome.Action.ShouldBe(ReattachAction.FailedVmMissing);
        env.Gcp.EnsureBucketCalls.ShouldBe(0, "a read-only look must not create a bucket");
    }

    // ---- a user cancel that lands while the reattach is driving the run (cold review of #59) ----

    private static async Task<Task<IReadOnlyList<ReattachOutcome>>> ReattachParkedInItsLookAsync(Env env, string jobId, CancellationToken shutdown)
    {
        env.Gcp.WithHungCalls(1);
        var launch = env.Reconciler().ReattachAsync(shutdown);
        await env.Active.WhenActiveAsync(jobId);
        await env.Gcp.WhenHungAsync().WaitAsync(RunSignals.Safety);

        env.Gcp.HungCalls.ShouldBe(1, "precondition: the reattach is parked inside its look at the cloud");
        return launch;
    }

    [Fact]
    public async Task A_cancel_that_fails_after_it_stopped_the_reattach_ends_the_wait_at_once_and_leaves_the_row_for_the_next_launch()
    {
        var env = new Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-cf", JobPhase.Running, vm: true);
        var launch = await ReattachParkedInItsLookAsync(env, "job-cf", CancellationToken.None);

        var network = new CloudOperationException(new CloudError(null, null, "no route"), CloudErrorKind.Network);
        await Should.ThrowAsync<CloudOperationException>(() => env.Active.CancelAsync("job-cf", () => throw network));
        var outcomes = await launch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        outcomes.Single().FinalPhase.ShouldBe(JobPhase.Running, "the cancel wrote nothing, so the outcome says what the row still is");
        JobStateMachine.IsTerminal(env.Row("job-cf").Phase).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancel_still_working_when_the_app_shuts_down_does_not_hold_the_reattach_up()
    {
        var env = new Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-sd", JobPhase.Running, vm: true);
        using var shutdown = new CancellationTokenSource();
        var launch = await ReattachParkedInItsLookAsync(env, "job-sd", shutdown.Token);
        // The cancel's own work runs only after the driver has ended (and left the registry): reaching it is the signal that the cancel has stopped the driver.
        var driverStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = env.Active.CancelAsync("job-sd", () =>
        {
            driverStopped.TrySetResult();
            return Task.Delay(Timeout.Infinite);
        });
        await driverStopped.Task.WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken);

        env.Active.IsActive("job-sd").ShouldBeFalse("precondition: the cancel has stopped the driver");
        await shutdown.CancelAsync();

        var ended = await Record.ExceptionAsync(() => launch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        ended.ShouldBeAssignableTo<OperationCanceledException>("shutdown ends the wait; a timeout here means the reattach was held up");
        GC.KeepAlive(cancel);
    }

    [Fact]
    public async Task A_user_cancel_of_a_reattach_that_was_finishing_a_cancel_reports_CancelFinished_not_Resumed()
    {
        var env = new Env(new FakeGcp().WithWorker(FakeWorkerMode.Never));
        await env.SeedAsync("job-cc", JobPhase.Cancelling, vm: true);
        // Counting from here, read 1 is the reconciler's own; read 2 is the runner's cancel looking at the row, with the driver's token and no catch around it,
        // so a stop there leaves the driver without an outcome and the answer comes from OutcomeOfCancelledAsync.
        var parked = new TaskCompletionSource();
        env.Repo.BeforeGetAll = async (call, token) =>
        {
            if (call == 2)
            {
                parked.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var launch = env.Reconciler().ReattachAsync(CancellationToken.None);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The user's cancel ends the run (a cancel that wrote nothing would leave the row Cancelling, which is CancelInterrupted, #551).
        await env.Active.CancelAsync("job-cc", () => env.Repo.UpsertAsync(env.Row("job-cc") with { Phase = JobPhase.Cancelled }, CancellationToken.None));
        var outcomes = await launch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        outcomes.Single().Action.ShouldBe(ReattachAction.CancelFinished, "the reattach was finishing a cancel, not resuming a run");
        outcomes.Single().FinalPhase.ShouldBe(JobPhase.Cancelled);
    }
}
