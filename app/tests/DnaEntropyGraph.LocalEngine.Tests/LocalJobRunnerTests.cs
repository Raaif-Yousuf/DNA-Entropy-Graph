using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.LocalEngine;
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

    private LocalJobRunner Runner(FakeProcessLauncher launcher, TimeSpan? grace = null)
        => new(launcher, _runs, Layout, RunsDirectory, (_, phase) => _phases.Add(phase))
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            CancelGrace = grace ?? TimeSpan.FromMilliseconds(300),
        };

    private LocalJobRequest Request(string jobId = JobId, string? outputFolder = null) => new(
        jobId,
        new RunOptions { ModelId = "mock", RunTarget = "This PC", Predictor = PredictorSelection.MockTestPredictor },
        new StagedInput(_stagedInput, "locus.fasta"),
        "install-1",
        "0.0.1",
        outputFolder ?? OutputParent);

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

        var result = await Runner(launcher).RunAsync(Request("..") , CancellationToken.None);

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
