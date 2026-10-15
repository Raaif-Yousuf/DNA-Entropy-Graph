using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.LocalEngine;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.LocalEngine.Tests;

/// <summary>
/// Issue #174's test: the REAL Python worker with the mock predictor, through the real <see cref="SystemProcessLauncher"/>. It needs a
/// Python that can import <c>dna_entropy</c>, so it runs only when <c>DEG_TEST_WORKER_PYTHON</c> names one (and, for a source checkout,
/// <c>DEG_TEST_WORKER_SRC</c> names <c>worker\src</c>); otherwise it does nothing and says so by passing no assertions.
/// <c>scripts/heavy.py</c> callers set both: <c>$env:DEG_TEST_WORKER_PYTHON = "...\worker\.venv\Scripts\python.exe"</c>.
/// </summary>
public sealed class RealWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-realworker-" + Guid.NewGuid().ToString("N"));

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

    private static LocalEngineLayout? RealLayout()
    {
        var python = Environment.GetEnvironmentVariable("DEG_TEST_WORKER_PYTHON");
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
        {
            return null;
        }

        var src = Environment.GetEnvironmentVariable("DEG_TEST_WORKER_SRC");
        return new LocalEngineLayout(
            @"C:\unused-engine",
            python,
            string.IsNullOrWhiteSpace(src) ? null : new Dictionary<string, string> { ["PYTHONPATH"] = src });
    }

    [Fact]
    public async Task The_real_worker_runs_a_mock_job_and_its_tracks_reach_the_output_folder()
    {
        var layout = RealLayout();
        if (layout is null)
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "staged.fasta");
        File.WriteAllText(input, ">locus\n" + string.Concat(Enumerable.Repeat("ACGT", 60)) + "\n");
        var runs = new RealRuns();
        runs.Seed(new RunRecord("real-1", JobPhase.Draft, DateTimeOffset.UtcNow, Target: "local"));
        var runner = new LocalJobRunner(new SystemProcessLauncher(), runs, layout, Path.Combine(_root, "runs"));
        var request = new LocalJobRequest(
            "real-1",
            new RunOptions { ModelId = "evo2_7b", RunTarget = "This PC", Predictor = PredictorSelection.MockTestPredictor, ContextLength = 128 },
            new StagedInput(input, "locus.fasta"),
            "install-1",
            "0.0.1",
            Path.Combine(_root, "out"));

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.ErrorDetail);
        var outDir = runs.Latest("real-1").OutputDir!;
        Directory.EnumerateFiles(outDir, "*.bedgraph", SearchOption.AllDirectories).ShouldNotBeEmpty();
        runs.Latest("real-1").WorkerVersion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_real_worker_answers_the_health_check_with_the_apps_version()
    {
        var layout = RealLayout();
        if (layout is null)
        {
            return;
        }

        var manager = new LocalEngineManager(new NoGpu(), new FixedDisk(), new SystemProcessLauncher(), layout, "0.0.1");

        var health = await manager.CheckHealthAsync(CancellationToken.None);

        health.State.ShouldBe(LocalEngineState.Healthy, health.Detail);
        health.CudaAvailable.ShouldBeFalse(); // this check runs on a CPU-only worker env; a GPU PC is the ToTest row.
    }

    private sealed class NoGpu : IGpuDetector
    {
        public Task<GpuDetection> DetectAsync(CancellationToken cancellationToken) => Task.FromResult(new GpuDetection(GpuDetectionStatus.NoNvidia, []));
    }

    private sealed class FixedDisk : IFreeDiskSpace
    {
        public long FreeBytes(string path) => 100L << 30;
    }

    private sealed class RealRuns : IRunRepository
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
