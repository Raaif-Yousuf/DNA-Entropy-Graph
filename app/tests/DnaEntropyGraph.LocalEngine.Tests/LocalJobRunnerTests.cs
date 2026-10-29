using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Cloud.Tests;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.LocalEngine.Tests;

public sealed class LocalJobRunnerTests : IDisposable
{
    private const string JobId = "20261015-120000-abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-localrunner-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryRuns _runs = new();
    private readonly List<JobPhase> _phases = [];
    private readonly string _stagedInput;

    public LocalJobRunnerTests()
    {
        Directory.CreateDirectory(_root);
        _stagedInput = Path.Combine(_root, "staged", "locus.fasta");
        Directory.CreateDirectory(Path.GetDirectoryName(_stagedInput)!);
        File.WriteAllText(_stagedInput, ">locus\nACGTACGTACGT\n");
        _runs.Seed(new RunRecord(JobId, JobPhase.Draft, DateTimeOffset.UtcNow, Target: RunTargets.LocalRecord));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string RunsDirectory => Path.Combine(_root, "runs");

    private string OutputParent => Path.Combine(_root, "out");

    private static readonly LocalEngineLayout Layout = new(@"C:\data\engine");

    // Real-time runner (#841), kept where the real clock cannot fail correct code: a short CancelGrace (50 to 300 ms) is used only by tests that let it run OUT and assert the
    // kill that follows (a loaded machine only makes the kill later, never absent), and the one test that asserts "not killed during the grace" gives it 30 s. Every
    // test that needs a deadline to pass or NOT pass at an exact moment uses TimedRunner and the virtual clock.
    private LocalJobRunner Runner(FakeProcessLauncher launcher, TimeSpan? grace = null, LocalEngineLayout? layout = null)
        => new(launcher, _runs, layout ?? Layout, RunsDirectory, (_, phase) => _phases.Add(phase))
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            CancelGrace = grace ?? TimeSpan.FromMilliseconds(300),
        };

    /// <summary>A runner on a clock that stands still until the test moves it: every deadline below is virtual time (#525).</summary>
    private LocalJobRunner TimedRunner(FakeProcessLauncher launcher, VirtualTimeProvider clock)
        => new(launcher, _runs, Layout, RunsDirectory, (_, phase) => _phases.Add(phase))
        {
            Clock = clock,
            PollInterval = TimeSpan.FromSeconds(1),
            CancelGrace = TimeSpan.FromSeconds(5),
            FirstHeartbeatDeadline = TimeSpan.FromSeconds(60),
            StaleHeartbeat = TimeSpan.FromSeconds(120),
        };

    private LocalJobRequest Request(string jobId = JobId, string? outputFolder = null, string? cudaDevice = null, int maxRunMinutes = 240) => new(
        jobId,
        new RunOptions { ModelId = "mock", RunTarget = "This PC", Predictor = PredictorSelection.MockTestPredictor, MaxRunDurationMinutes = maxRunMinutes },
        new StagedInput(_stagedInput, "locus.fasta"),
        "install-1",
        "0.0.1",
        outputFolder ?? OutputParent,
        cudaDevice);

    private static string RootOf(ProcessSpec spec) => spec.Arguments[spec.Arguments.ToList().IndexOf("--root") + 1];

    private static (string Json, string Sha, long Bytes) WriteOutput(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var bytes = Encoding.UTF8.GetBytes(content);
        return (relative, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    }

    private static void WriteResult(string root, string status, object[] inputs, string? errorCode = null)
    {
        var doc = new Dictionary<string, object?> { ["schema"] = 1, ["status"] = status, ["inputs"] = inputs };
        if (errorCode is not null)
        {
            doc["error"] = new { code = errorCode, message = "m" };
        }

        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(doc));
    }

    /// <summary>A worker that finishes at once, writing one good track.</summary>
    private static FakeProcessLauncher HappyWorker(string content = "locus 0 12 1.5\n", string? corruptSha = null)
        => new(spec =>
        {
            var root = RootOf(spec);
            var (rel, sha, bytes) = WriteOutput(root, "output/locus/locus.entropy.bedgraph", content);
            WriteResult(root, "done", [new { id = "in1", status = "done", files = new[] { new { path = rel, sha256 = corruptSha ?? sha, bytes } } }]);
            var process = new FakeProcess();
            process.End(0);
            return process;
        });

    [Fact]
    public async Task A_good_run_writes_the_manifest_starts_the_worker_on_the_job_folder_and_copies_the_outputs()
    {
        var launcher = HappyWorker();

        var result = await Runner(launcher).RunAsync(Request(), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        var jobDir = Path.Combine(RunsDirectory, JobId);
        File.Exists(Path.Combine(jobDir, "input", "locus.fasta")).ShouldBeTrue();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDir, "manifest.json")));
        manifest.RootElement.GetProperty("store").GetProperty("kind").GetString().ShouldBe("localdir");
        manifest.RootElement.GetProperty("store").GetProperty("root").GetString().ShouldBe(jobDir);
        manifest.RootElement.GetProperty("jobId").GetString().ShouldBe(JobId);
        manifest.RootElement.GetProperty("predictor").GetProperty("kind").GetString().ShouldBe("mock");

        var spec = launcher.Started.Single();
        spec.FileName.ShouldBe(Layout.PythonExe);
        spec.Arguments.ShouldBe(["-m", "dna_entropy.worker.cli", "run", "--manifest", Path.Combine(jobDir, "manifest.json"), "--store", "localdir", "--root", jobDir]);

        var row = _runs.Latest(JobId);
        row.Phase.ShouldBe(JobPhase.Completed);
        row.Target.ShouldBe("local");
        row.ManifestJson.ShouldNotBeNull();
        File.ReadAllText(Path.Combine(row.OutputDir!, "locus", "locus.entropy.bedgraph")).ShouldBe("locus 0 12 1.5\n");
        _phases.ShouldBe([JobPhase.Preparing, JobPhase.Running, JobPhase.Finalizing, JobPhase.Downloading, JobPhase.Completed]);
    }

    [Fact]
    public async Task The_users_staged_input_is_copied_never_moved_and_the_run_folder_is_inside_the_chosen_output_folder()
    {
        await Runner(HappyWorker()).RunAsync(Request(), CancellationToken.None);

        File.Exists(_stagedInput).ShouldBeTrue();
        var outDir = _runs.Latest(JobId).OutputDir!;
        Path.GetDirectoryName(outDir).ShouldBe(OutputParent);
        Path.GetFileName(outDir).ShouldBe("locus");
    }

    [Fact]
    public async Task An_input_the_store_already_put_in_the_job_folder_is_used_in_place_not_copied_onto_itself()
    {
        var inPlace = Path.Combine(RunsDirectory, JobId, "input", "locus.fasta");
        Directory.CreateDirectory(Path.GetDirectoryName(inPlace)!);
        File.WriteAllText(inPlace, ">locus\nACGT\n");
        var request = Request() with { Input = new StagedInput(inPlace, "locus.fasta") };

        var result = await Runner(HappyWorker()).RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        File.ReadAllText(inPlace).ShouldBe(">locus\nACGT\n");
    }

    [Fact]
    public async Task A_second_run_of_the_same_input_gets_its_own_folder()
    {
        await Runner(HappyWorker()).RunAsync(Request(), CancellationToken.None);
        _runs.Seed(new RunRecord("job-2", JobPhase.Draft, DateTimeOffset.UtcNow, Target: "local"));

        await Runner(HappyWorker()).RunAsync(Request("job-2"), CancellationToken.None);

        Path.GetFileName(_runs.Latest("job-2").OutputDir!).ShouldBe("locus_2");
    }

    [Fact]
    public async Task The_run_name_template_names_the_folder_the_same_way_a_cloud_run_does()
    {
        // Issue #514: both runners name the folder through RunOutputFolders.RunFolderName, so they cannot drift.
        var request = Request() with { Options = Request().Options with { NameTemplate = "{file}_{model}" } };

        await Runner(HappyWorker()).RunAsync(request, CancellationToken.None);

        var folder = Path.GetFileName(_runs.Latest(JobId).OutputDir!);
        folder.ShouldBe("locus_mock");
        folder.ShouldBe(RunOutputFolders.RunFolderName(request.Options, request.Input, DateTime.Now));
    }

    [Fact]
    public async Task A_worker_that_exits_with_no_result_is_a_local_worker_crash_and_leaves_no_empty_output_folder()
    {
        var launcher = new FakeProcessLauncher(_ =>
        {
            var p = new FakeProcess();
            p.End(1);
            return p;
        });

        var result = await Runner(launcher).RunAsync(Request(), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.ErrorCode.ShouldBe(RunErrorCodes.LocalWorkerCrashed);
        result.ErrorDetail!.ShouldContain("exit code 1");
        _runs.Latest(JobId).OutputDir.ShouldBeNull();
        Directory.EnumerateDirectories(OutputParent).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_result_maps_the_workers_code_and_keeps_the_partial_files()
    {
        var launcher = new FakeProcessLauncher(spec =>
        {
            var root = RootOf(spec);
            var (rel, sha, bytes) = WriteOutput(root, "output/locus/partial.tsv", "x\n");
            WriteResult(root, "failed", [new { id = "in1", status = "failed", files = new[] { new { path = rel, sha256 = sha, bytes } }, error = new { code = "MODEL_OOM", message = "m" } }], "MODEL_OOM");
            var p = new FakeProcess();
            p.End(2);
            return p;
        });

        var result = await Runner(launcher).RunAsync(Request(), CancellationToken.None);

        result.ErrorCode.ShouldBe(RunErrorCodes.ModelOom);
        File.Exists(Path.Combine(_runs.Latest(JobId).OutputDir!, "locus", "partial.tsv")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_file_that_does_not_match_its_reported_checksum_fails_the_run_as_corrupt()
    {
        var result = await Runner(HappyWorker(corruptSha: new string('0', 64))).RunAsync(Request(), CancellationToken.None);

        result.ErrorCode.ShouldBe(RunErrorCodes.DownloadCorrupt);
        Directory.EnumerateFiles(_runs.Latest(JobId).OutputDir!, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("output/../escape.txt")]
    [InlineData("../escape.txt")]
    [InlineData("output/a\\b.txt")]
    [InlineData("input/locus.fasta")]
    public async Task A_result_path_that_is_not_a_plain_output_path_is_refused(string path)
    {
        var launcher = new FakeProcessLauncher(spec =>
        {
            WriteResult(RootOf(spec), "done", [new { id = "in1", status = "done", files = new[] { new { path, sha256 = (string?)null, bytes = (long?)null } } }]);
            var p = new FakeProcess();
            p.End(0);
            return p;
        });

        var result = await Runner(launcher).RunAsync(Request(), CancellationToken.None);

        result.ErrorCode.ShouldBe(RunErrorCodes.WorkerFailed);
        File.Exists(Path.Combine(_root, "escape.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_python_that_cannot_start_fails_with_engine_not_ready_and_no_leftover_folder()
    {
        var result = await Runner(new FakeProcessLauncher(_ => null)).RunAsync(Request(), CancellationToken.None);

        result.ErrorCode.ShouldBe(RunErrorCodes.LocalEngineNotReady);
        Directory.EnumerateDirectories(OutputParent).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_output_folder_that_cannot_be_made_fails_before_the_worker_starts()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");
        var launcher = HappyWorker();

        var result = await Runner(launcher).RunAsync(Request(outputFolder: Path.Combine(blocker, "sub")), CancellationToken.None);

        result.ErrorCode.ShouldBe(RunErrorCodes.OutputFolderUnusable);
        launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_id_that_climbs_out_of_the_runs_folder_is_refused_before_anything_is_written()
    {
        var launcher = HappyWorker();
        _runs.Seed(new RunRecord("..", JobPhase.Draft, DateTimeOffset.UtcNow, Target: "local"));

        var result = await Runner(launcher).RunAsync(Request(".."), CancellationToken.None);

        result.ErrorCode.ShouldBe(RunErrorCodes.ManifestInvalid);
        launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_heartbeat_in_status_json_reaches_the_run_row_while_the_worker_runs()
    {
        var worker = new FakeProcess();
        var launcher = new FakeProcessLauncher(spec =>
        {
            File.WriteAllText(Path.Combine(RootOf(spec), "status.json"), "{\"stage\":\"running\",\"heartbeatSeq\":7,\"updatedAt\":\"2026-10-15T12:00:00Z\",\"worker\":{\"version\":\"0.0.1\"}}");
            return worker;
        });

        var run = Runner(launcher).RunAsync(Request(), CancellationToken.None);
        await WaitUntilAsync(() => _runs.Latest(JobId).LastProgressSeq == 7);
        worker.End(1);
        await run;

        _runs.Latest(JobId).WorkerVersion.ShouldBe("0.0.1");
        _runs.Latest(JobId).LastHeartbeatAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_cancel_writes_control_cancel_and_waits_for_the_worker_to_stop_by_itself()
    {
        var worker = new FakeProcess();
        string? root = null;
        var launcher = new FakeProcessLauncher(spec =>
        {
            root = RootOf(spec);
            return worker;
        });
        using var cts = new CancellationTokenSource();
        var run = Runner(launcher, grace: TimeSpan.FromSeconds(30)).RunAsync(Request(), cts.Token);
        await WaitUntilAsync(() => _runs.Latest(JobId).Phase == JobPhase.Running);

        await cts.CancelAsync();
        await WaitUntilAsync(() => File.Exists(Path.Combine(root!, "control", "cancel")));
        worker.Killed.ShouldBeFalse();
        worker.End(3);

        await Should.ThrowAsync<OperationCanceledException>(run);
        worker.Killed.ShouldBeFalse();
        new FileInfo(Path.Combine(root!, "control", "cancel")).Length.ShouldBe(0);
    }

    [Fact]
    public async Task A_worker_that_ignores_the_cancel_has_its_whole_process_tree_killed_after_the_grace()
    {
        var worker = new FakeProcess();
        var launcher = new FakeProcessLauncher(_ => worker);
        using var cts = new CancellationTokenSource();
        var run = Runner(launcher, grace: TimeSpan.FromMilliseconds(100)).RunAsync(Request(), cts.Token);
        await WaitUntilAsync(() => _runs.Latest(JobId).Phase == JobPhase.Running);

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(run);
        worker.Killed.ShouldBeTrue();
        worker.KilledTree.ShouldBeTrue();
    }

    [Fact]
    public async Task Recording_a_cancel_goes_through_Cancelling_to_Cancelled()
    {
        var worker = new FakeProcess();
        using var cts = new CancellationTokenSource();
        var runner = Runner(new FakeProcessLauncher(_ => worker), grace: TimeSpan.FromMilliseconds(50));
        var run = runner.RunAsync(Request(), cts.Token);
        await WaitUntilAsync(() => _runs.Latest(JobId).Phase == JobPhase.Running);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(run);

        await runner.RecordCancelledAsync(JobId);

        _runs.Latest(JobId).Phase.ShouldBe(JobPhase.Cancelled);
        _phases.TakeLast(2).ShouldBe([JobPhase.Cancelling, JobPhase.Cancelled]);
    }

    [Fact]
    public async Task A_run_already_terminal_is_not_moved_by_a_late_cancel_record()
    {
        var runner = Runner(HappyWorker());
        await runner.RunAsync(Request(), CancellationToken.None);

        await runner.RecordCancelledAsync(JobId);

        _runs.Latest(JobId).Phase.ShouldBe(JobPhase.Completed);
    }

    private static void WriteHeartbeat(string root, int seq)
        => File.WriteAllText(Path.Combine(root, "status.json"), "{\"stage\":\"running\",\"heartbeatSeq\":" + seq + ",\"updatedAt\":\"2026-10-15T12:00:00Z\",\"worker\":{\"version\":\"0.0.1\"}}");

    /// <summary>
    /// Moves the virtual clock one poll at a time (letting the runner's loop run between steps) until the condition holds.
    /// Real time here (#841) is only a yield between steps and a 30 s safety net: the outcome follows from how far the virtual clock moved, so a starved
    /// thread makes the loop take more steps and never changes which state the run ends in.
    /// </summary>
    private static async Task AdvanceUntilAsync(VirtualTimeProvider clock, Func<bool> condition, Action? eachStep = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached on virtual time");
            }

            eachStep?.Invoke();
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(3);
        }
    }

    [Fact]
    public async Task A_worker_that_never_reports_a_first_heartbeat_is_killed_with_its_tree_and_the_run_fails_with_a_named_code()
    {
        // Observable: without the deadline this run never ends (the worker never exits and never writes a status).
        var clock = new VirtualTimeProvider();
        var worker = new FakeProcess();
        var run = TimedRunner(new FakeProcessLauncher(_ => worker), clock).RunAsync(Request(), CancellationToken.None);
        await WaitUntilAsync(() => _runs.Latest(JobId).Phase == JobPhase.Running);

        clock.Advance(TimeSpan.FromSeconds(55));
        // Negative evidence, real time (#841): the runner must NOT kill the worker, so a window too short to let it react can only hide a regression, never fail correct code.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        worker.Killed.ShouldBeFalse("55 s is inside the 60 s first-heartbeat deadline");
        await AdvanceUntilAsync(clock, () => run.IsCompleted);

        var result = await run;
        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.ErrorCode.ShouldBe(RunErrorCodes.LocalWorkerNoHeartbeat);
        worker.KilledTree.ShouldBeTrue();
        _runs.Latest(JobId).Phase.ShouldBe(JobPhase.Failed);
        _runs.Latest(JobId).ErrorCode.ShouldBe(RunErrorCodes.LocalWorkerNoHeartbeat);
        _runs.Latest(JobId).OutputDir.ShouldBeNull();
        Directory.EnumerateDirectories(OutputParent).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_heartbeat_that_stops_changing_is_stale_after_the_window_and_the_worker_is_killed()
    {
        var clock = new VirtualTimeProvider();
        var worker = new FakeProcess();
        string? root = null;
        var run = TimedRunner(new FakeProcessLauncher(spec =>
        {
            root = RootOf(spec);
            WriteHeartbeat(root, 1);
            return worker;
        }), clock).RunAsync(Request(), CancellationToken.None);
        await AdvanceUntilAsync(clock, () => _runs.Latest(JobId).LastProgressSeq == 1);

        await AdvanceUntilAsync(clock, () => run.IsCompleted);

        var result = await run;
        result.ErrorCode.ShouldBe(RunErrorCodes.LocalWorkerStalled);
        worker.KilledTree.ShouldBeTrue();
        _runs.Latest(JobId).ErrorCode.ShouldBe(RunErrorCodes.LocalWorkerStalled);
    }

    [Fact]
    public async Task A_worker_whose_heartbeat_keeps_changing_is_never_called_stale_and_finishes_normally()
    {
        var clock = new VirtualTimeProvider();
        var worker = new FakeProcess();
        string? root = null;
        var seq = 0;
        var run = TimedRunner(new FakeProcessLauncher(spec =>
        {
            root = RootOf(spec);
            return worker;
        }), clock).RunAsync(Request(), CancellationToken.None);
        await WaitUntilAsync(() => root is not null);

        // 300 virtual seconds: 2.5 stale windows, each poll with a new heartbeat.
        for (var step = 0; step < 300; step++)
        {
            WriteHeartbeat(root!, ++seq);
            clock.Advance(TimeSpan.FromSeconds(1));
            // A yield, not a wait for anything (#841): every clock move is preceded by a new heartbeat, so a runner that polls late still sees a changed one.
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        worker.Killed.ShouldBeFalse();
        var (rel, sha, bytes) = WriteOutput(root!, "output/locus/locus.entropy.bedgraph", "locus 0 12 1.5\n");
        WriteResult(root!, "done", [new { id = "in1", status = "done", files = new[] { new { path = rel, sha256 = sha, bytes } } }]);
        worker.End(0);
        (await run).FinalPhase.ShouldBe(JobPhase.Completed);
    }

    [Fact]
    public async Task A_worker_that_beats_forever_is_stopped_at_the_run_cap_without_relying_on_the_worker_side_cap()
    {
        var clock = new VirtualTimeProvider();
        var worker = new FakeProcess();
        string? root = null;
        var seq = 0;
        var run = TimedRunner(new FakeProcessLauncher(spec =>
        {
            root = RootOf(spec);
            return worker;
        }), clock).RunAsync(Request(maxRunMinutes: 10), CancellationToken.None);
        await WaitUntilAsync(() => root is not null);

        await AdvanceUntilAsync(clock, () => run.IsCompleted, () => WriteHeartbeat(root!, ++seq));

        var result = await run;
        result.ErrorCode.ShouldBe(RunErrorCodes.LocalWorkerTimedOut);
        worker.KilledTree.ShouldBeTrue();
        // Ten minutes plus the margin the worker's own cap gets to fire first; never earlier than the limit.
        clock.GetUtcNow().ShouldBeGreaterThanOrEqualTo(new DateTimeOffset(2026, 10, 2, 8, 10, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task The_worker_is_started_on_the_chosen_graphics_card_and_with_the_python_variables_cleared()
    {
        var launcher = HappyWorker();

        await Runner(launcher).RunAsync(Request(cudaDevice: "GPU-1f2e3d4c"), CancellationToken.None);

        var spec = launcher.Started.Single();
        spec.Environment!["CUDA_VISIBLE_DEVICES"].ShouldBe("GPU-1f2e3d4c");
        spec.ClearEnvironment.ShouldNotBeNull();
        spec.ClearEnvironment!.ShouldBe(["PYTHONPATH", "PYTHONHOME", "VIRTUAL_ENV"], ignoreOrder: true);
    }

    [Fact]
    public async Task With_no_card_chosen_CUDA_VISIBLE_DEVICES_is_not_set_and_a_development_PYTHONPATH_is_kept()
    {
        var launcher = HappyWorker();
        var layout = new LocalEngineLayout(@"C:\data\engine", null, new Dictionary<string, string> { ["PYTHONPATH"] = @"C:\src\worker" });

        await Runner(launcher, layout: layout).RunAsync(Request(), CancellationToken.None);

        var spec = launcher.Started.Single();
        spec.Environment!.ContainsKey("CUDA_VISIBLE_DEVICES").ShouldBeFalse();
        spec.Environment["PYTHONPATH"].ShouldBe(@"C:\src\worker");
        spec.ClearEnvironment!.ShouldBe(["PYTHONHOME", "VIRTUAL_ENV"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_cancel_that_leaves_nothing_clears_the_rows_output_folder()
    {
        var worker = new FakeProcess();
        using var cts = new CancellationTokenSource();
        var run = Runner(new FakeProcessLauncher(_ => worker), grace: TimeSpan.FromMilliseconds(50)).RunAsync(Request(), cts.Token);
        await WaitUntilAsync(() => _runs.Latest(JobId).Phase == JobPhase.Running);
        _runs.Latest(JobId).OutputDir.ShouldNotBeNull();

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(run);

        _runs.Latest(JobId).OutputDir.ShouldBeNull();
        Directory.EnumerateDirectories(OutputParent).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_nonzero_exit_after_a_finished_result_json_still_completes_the_run_with_a_note_and_a_log_line()
    {
        // DECISION (agent-made, reversible): result.json is the worker's verdict and the files are verified against it, so a late nonzero exit
        // (a crash while closing CUDA is the usual cause) does not throw away good results; the row carries a note code and the exit code is logged.
        var log = new RecordingLog();
        var launcher = new FakeProcessLauncher(spec =>
        {
            var root = RootOf(spec);
            var (rel, sha, bytes) = WriteOutput(root, "output/locus/locus.entropy.bedgraph", "locus 0 12 1.5\n");
            WriteResult(root, "done", [new { id = "in1", status = "done", files = new[] { new { path = rel, sha256 = sha, bytes } } }]);
            var process = new FakeProcess();
            process.End(-1073740791);
            return process;
        });
        var runner = new LocalJobRunner(launcher, _runs, Layout, RunsDirectory, (_, phase) => _phases.Add(phase), log) { PollInterval = TimeSpan.FromMilliseconds(20) };

        var result = await runner.RunAsync(Request(), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        _runs.Latest(JobId).ErrorCode.ShouldBe(RunErrorCodes.LocalWorkerExitedNonzero);
        _runs.Latest(JobId).ErrorDetail!.ShouldContain("-1073740791");
        log.Entries.ShouldContain(e => e.JobId == JobId && e.ErrorClass.Contains("exit code -1073740791"));
    }

    [Fact]
    public async Task A_clean_exit_adds_no_note()
    {
        await Runner(HappyWorker()).RunAsync(Request(), CancellationToken.None);

        _runs.Latest(JobId).ErrorCode.ShouldBeNull();
    }

    private sealed class RecordingLog : IDiagnosticsLog
    {
        public List<(string Source, string? JobId, string ErrorClass)> Entries { get; } = [];

        public void Warning(string source, string? jobId, string errorClass) => Entries.Add((source, jobId, errorClass));
    }
    [Theory]
    [InlineData(JobPhase.Draft)]
    [InlineData(JobPhase.Preparing)]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Finalizing)]
    [InlineData(JobPhase.Downloading)]
    public async Task On_launch_a_local_run_left_non_terminal_with_no_result_is_failed_as_interrupted(JobPhase phase)
    {
        // MEASURED 2026-10-15 (issue #734): a killed app left the row Running for ever; nothing looked at Target=local rows.
        _runs.Seed(new RunRecord("old-1", phase, DateTimeOffset.UtcNow.AddMinutes(-5), Target: RunTargets.LocalRecord));

        var count = await Runner(HappyWorker()).ReconcileInterruptedAsync(CancellationToken.None);

        count.ShouldBe(2, "this row plus the Draft row the fixture seeds");
        var row = _runs.Latest("old-1");
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe(RunErrorCodes.LocalRunInterrupted);
        row.FinishedAt.ShouldNotBeNull();
        _phases.ShouldContain(JobPhase.Failed);
    }

    [Fact]
    public async Task On_launch_a_local_run_whose_worker_finished_before_the_app_died_is_completed_from_its_result_json()
    {
        var jobDir = Path.Combine(RunsDirectory, "old-2");
        var (rel, sha, bytes) = WriteOutput(jobDir, "output/locus/locus.entropy.bedgraph", "locus 0 12 1.5\n");
        WriteResult(jobDir, "done", [new { id = "in1", status = "done", files = new[] { new { path = rel, sha256 = sha, bytes } } }]);
        var outputDir = Path.Combine(OutputParent, "locus");
        Directory.CreateDirectory(outputDir);
        _runs.Seed(new RunRecord("old-2", JobPhase.Running, DateTimeOffset.UtcNow.AddMinutes(-5), Target: RunTargets.LocalRecord, OutputDir: outputDir, OptionsJson: OptionsFor(OutputParent)));

        await Runner(HappyWorker()).ReconcileInterruptedAsync(CancellationToken.None);

        _runs.Latest("old-2").Phase.ShouldBe(JobPhase.Completed);
        File.ReadAllText(Path.Combine(outputDir, "locus", "locus.entropy.bedgraph")).ShouldBe("locus 0 12 1.5\n");
    }

    private static string OptionsFor(string outputFolder)
        => RunOptionsJson.Serialize(new RunOptions { ModelId = "mock", RunTarget = "This PC", Predictor = PredictorSelection.MockTestPredictor, OutputFolder = outputFolder });

    [Fact]
    public async Task On_launch_a_finished_row_whose_output_folder_is_a_drive_root_still_completes()
    {
        // Reconcile copies the run's own results into the folder the app made; it deletes nothing, so a drive-root output folder is fine.
        var jobDir = Path.Combine(RunsDirectory, "old-5");
        var (rel, sha, bytes) = WriteOutput(jobDir, "output/locus/locus.entropy.bedgraph", "locus 0 12 1.5\n");
        WriteResult(jobDir, "done", [new { id = "in1", status = "done", files = new[] { new { path = rel, sha256 = sha, bytes } } }]);
        var outputDir = Path.Combine(OutputParent, "locus");
        Directory.CreateDirectory(outputDir);
        _runs.Seed(new RunRecord("old-5", JobPhase.Running, DateTimeOffset.UtcNow.AddMinutes(-5), Target: RunTargets.LocalRecord, OutputDir: outputDir, OptionsJson: OptionsFor(Path.GetPathRoot(outputDir)!)));

        await Runner(HappyWorker()).ReconcileInterruptedAsync(CancellationToken.None);

        _runs.Latest("old-5").Phase.ShouldBe(JobPhase.Completed);
        File.Exists(Path.Combine(outputDir, "locus", "locus.entropy.bedgraph")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("x\\..\\other")]
    [InlineData("..")]
    [InlineData(".")]
    public async Task A_job_id_that_is_not_one_plain_name_is_refused_and_creates_nothing(string jobId)
    {
        var result = await Runner(HappyWorker()).RunAsync(Request(jobId), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.ErrorCode.ShouldBe(RunErrorCodes.ManifestInvalid);
        Directory.Exists(Path.Combine(RunsDirectory, "other")).ShouldBeFalse();
        Directory.Exists(Path.Combine(RunsDirectory, "x")).ShouldBeFalse();
    }
    [Theory]
    [InlineData("..\\victim")]
    [InlineData("..\\out2\\victim")]
    public async Task On_launch_a_finished_row_whose_output_folder_is_outside_the_output_root_copies_nothing_there_and_is_failed(string relative)
    {
        // Hard Rule 14: OutputDir comes from the history database; a corrupt row must not make the finish step write into any folder.
        var jobDir = Path.Combine(RunsDirectory, "old-4");
        var (rel, sha, bytes) = WriteOutput(jobDir, "output/locus/locus.entropy.bedgraph", "locus 0 12 1.5\n");
        WriteResult(jobDir, "done", [new { id = "in1", status = "done", files = new[] { new { path = rel, sha256 = sha, bytes } } }]);
        var victim = Path.GetFullPath(Path.Combine(OutputParent, relative));
        Directory.CreateDirectory(victim);
        Directory.CreateDirectory(OutputParent);
        _runs.Seed(new RunRecord("old-4", JobPhase.Running, DateTimeOffset.UtcNow.AddMinutes(-5), Target: RunTargets.LocalRecord, OutputDir: victim, OptionsJson: OptionsFor(OutputParent)));

        await Runner(HappyWorker()).ReconcileInterruptedAsync(CancellationToken.None);

        Directory.EnumerateFileSystemEntries(victim).ShouldBeEmpty();
        _runs.Latest("old-4").Phase.ShouldBe(JobPhase.Failed);
        _runs.Latest("old-4").ErrorCode.ShouldBe(RunErrorCodes.LocalRunInterrupted);
    }
    [Fact]
    public async Task On_launch_a_Cancelling_local_run_becomes_Cancelled_and_cloud_and_finished_rows_are_left_alone()
    {
        _runs.Seed(new RunRecord("old-3", JobPhase.Cancelling, DateTimeOffset.UtcNow, Target: RunTargets.LocalRecord));
        _runs.Seed(new RunRecord("cloud-1", JobPhase.Running, DateTimeOffset.UtcNow, Target: RunTargets.CloudRecord));
        _runs.Seed(new RunRecord("done-1", JobPhase.Completed, DateTimeOffset.UtcNow, Target: RunTargets.LocalRecord));

        var count = await Runner(HappyWorker()).ReconcileInterruptedAsync(CancellationToken.None);

        count.ShouldBe(2, "the seeded Draft of the fixture plus the Cancelling row");
        _runs.Latest("old-3").Phase.ShouldBe(JobPhase.Cancelled);
        _runs.Latest("cloud-1").Phase.ShouldBe(JobPhase.Running);
        _runs.Latest("done-1").Phase.ShouldBe(JobPhase.Completed);
    }
    // Waits for a state, not for a duration (#841): the 10 s deadline is only a safety net that turns a lost transition into a failure.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(10);
        }
    }

    private sealed class InMemoryRuns : IRunRepository
    {
        private readonly List<RunRecord> _rows = [];

        public void Seed(RunRecord row) => Upsert(row);

        public RunRecord Latest(string jobId)
        {
            lock (_rows)
            {
                return _rows.Last(r => r.JobId == jobId);
            }
        }

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
        {
            lock (_rows)
            {
                return Task.FromResult<IReadOnlyList<RunRecord>>(_rows.ToList());
            }
        }

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
        {
            Upsert(run);
            return Task.CompletedTask;
        }

        private void Upsert(RunRecord run)
        {
            lock (_rows)
            {
                _rows.RemoveAll(r => r.JobId == run.JobId);
                _rows.Add(run);
            }
        }
    }
}
