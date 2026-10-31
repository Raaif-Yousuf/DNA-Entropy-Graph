using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.LocalEngine;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #1086, wired-to-nothing: <c>RunProgressChangedMessage</c> had a receiver and no sender, so the run page's bar and detail line never moved while every test of
/// the receiver passed. These drive progress through the REAL composition (<see cref="ServiceRegistration.AddDnaEntropyGraph"/>): the observable that differs if the sender
/// were wired to nothing is <see cref="RunProgressViewModel.StatusDetailText"/> staying empty.
/// </summary>
public sealed class RunProgressWiringTests : IDisposable
{
    private const string JobId = "20261031-120000-abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-1086-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeWorkerProcess : IRunningProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> Exited => _exit.Task;

        public void End(int code) => _exit.TrySetResult(code);

        public void Kill(bool entireTree) => _exit.TrySetResult(-1);

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedLauncher(FakeWorkerProcess process, Action<string> onStart) : IProcessLauncher
    {
        public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null)
        {
            onStart(spec.Arguments[spec.Arguments.ToList().IndexOf("--root") + 1]);
            return process;
        }
    }

    [Fact]
    public async Task A_This_PC_runs_progress_in_status_json_moves_the_run_pages_detail_line_and_bar_through_the_real_container()
    {
        var worker = new FakeWorkerProcess();
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        services.AddSingleton<IProcessLauncher>(new ScriptedLauncher(worker, jobDir =>
            File.WriteAllText(
                Path.Combine(jobDir, "status.json"),
                "{\"stage\":\"running\",\"percent\":40,\"detail\":{\"window\":3,\"windows\":10},\"heartbeatSeq\":1,\"worker\":{\"version\":\"0.0.1\"}}")));
        await using var provider = services.BuildServiceProvider();

        var staged = Path.Combine(_root, "staged", "locus.fasta");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllTextAsync(staged, ">locus\nACGTACGTACGT\n", TestContext.Current.CancellationToken);
        await provider.GetRequiredService<IRunRepository>().UpsertAsync(new RunRecord(JobId, JobPhase.Draft, DateTimeOffset.UtcNow, Target: RunTargets.LocalRecord), CancellationToken.None);
        var page = provider.GetRequiredService<RunProgressViewModel>();
        page.JobId = JobId;
        page.StatusDetailText.ShouldBeNullOrEmpty("nothing has been published yet");

        var expectedText = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            provider.GetRequiredService<IStringResourceProvider>().GetString("ProgressAnalysingWindowCount"),
            3,
            10);
        expectedText.ShouldNotContain("ProgressAnalysingWindowCount", Case.Sensitive, "the resw line must resolve, or this test proves nothing");
        var runner = provider.GetRequiredService<LocalJobRunner>();
        var run = runner.RunAsync(
            new LocalJobRequest(
                JobId,
                new RunOptions { ModelId = "mock", RunTarget = "This PC", Predictor = PredictorSelection.MockTestPredictor },
                new StagedInput(staged, "locus.fasta"),
                "install-1",
                "0.0.1",
                Path.Combine(_root, "out")),
            CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (page.StatusDetailText != expectedText && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        var seenText = page.StatusDetailText;
        var seenFraction = page.FractionComplete;
        worker.End(1);
        await run;

        seenText.ShouldBe(expectedText);
        seenFraction.ShouldBe(0.4, 1e-9);
    }

    [Theory]
    [InlineData("miss", "ProgressCacheFirstRun")]
    [InlineData("unavailable", "ProgressCacheUnavailable")]
    [InlineData("hit", "ProgressModelLoading")]
    public async Task A_status_json_with_detail_cache_at_model_loading_reaches_the_run_page_as_that_caches_resw_line_through_the_real_container(string cache, string expectedKey)
    {
        // Issue #177, wired-to-nothing: the worker's detail.cache is read by WorkerStatusReader, carried by WorkerProgressTracker and picked by RunProgressViewModel; each hop is
        // unit-tested alone. Observable: with the Cache plumbing dropped the page shows the plain model-loading line on a miss.
        var worker = new FakeWorkerProcess();
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        services.AddSingleton<IProcessLauncher>(new ScriptedLauncher(worker, jobDir =>
            File.WriteAllText(
                Path.Combine(jobDir, "status.json"),
                "{\"stage\":\"model-loading\",\"percent\":0,\"detail\":{\"cache\":\"" + cache + "\"},\"heartbeatSeq\":1,\"worker\":{\"version\":\"0.0.1\"}}")));
        await using var provider = services.BuildServiceProvider();

        var staged = Path.Combine(_root, "staged", "locus.fasta");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllTextAsync(staged, ">locus\nACGTACGTACGT\n", TestContext.Current.CancellationToken);
        await provider.GetRequiredService<IRunRepository>().UpsertAsync(new RunRecord(JobId, JobPhase.Draft, DateTimeOffset.UtcNow, Target: RunTargets.LocalRecord), CancellationToken.None);
        var page = provider.GetRequiredService<RunProgressViewModel>();
        page.JobId = JobId;
        var strings = provider.GetRequiredService<IStringResourceProvider>();
        var expected = strings.GetString(expectedKey);
        (expected == expectedKey).ShouldBeFalse("the resw key must exist and resolve, or this test proves nothing");
        (expected == strings.GetString(expectedKey == "ProgressModelLoading" ? "ProgressCacheFirstRun" : "ProgressModelLoading")).ShouldBeFalse("the lines under test must differ from the plain one");

        var run = provider.GetRequiredService<LocalJobRunner>().RunAsync(
            new LocalJobRequest(
                JobId,
                new RunOptions { ModelId = "mock", RunTarget = "This PC", Predictor = PredictorSelection.MockTestPredictor },
                new StagedInput(staged, "locus.fasta"),
                "install-1",
                "0.0.1",
                Path.Combine(_root, "out")),
            CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (page.StatusDetailText != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        var seen = page.StatusDetailText;
        worker.End(1);
        await run;

        seen.ShouldBe(expected);
    }

    [Fact]
    public async Task A_cloud_run_whose_worker_status_stage_advances_moves_the_run_pages_detail_line_through_the_real_container()
    {
        // FakeGcp's status.json carries a stage and no percent or window counters (MEASURED 2026-10-31: WorkerStatusJson in FakeGcp.cs), so this proves the stage
        // path end to end: FakeGcp bucket -> the container's CloudJobRunner poll -> OnProgress -> RunProgressViewModel -> a Resources.resw line (never the raw stage id).
        const string project = "my-project";
        const string zone = "us-central1-a";
        const string jobId = "job-cloud-1";
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        services.AddSingleton<FakeGcp>(_ => new FakeGcp().WithWorker(FakeWorkerMode.StuckInModelLoading));
        await using var provider = services.BuildServiceProvider();
        var gcp = provider.GetRequiredService<FakeGcp>();
        using (var connection = provider.GetRequiredService<DnaEntropyGraph.Persistence.SqliteDatabase>().OpenConnection())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO Projects (ProjectId) VALUES ($id)";
            insert.Parameters.AddWithValue("$id", project);
            insert.ExecuteNonQuery();
        }

        var original = Path.Combine(_root, "user", "seq.gb");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        await File.WriteAllTextAsync(original, "LOCUS       seq\nORIGIN\n        1 acgtacgtac\n//\n", TestContext.Current.CancellationToken);
        var copy = await provider.GetRequiredService<IRunInputStore>().StageAsync(jobId, original, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "out");
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", InputPath = original, OutputFolder = output };
        var request = CloudJobRequestFactory.Create(options, jobId, new CloudScope(project), "install-1", "0.1.0", null, [copy], output);
        var runner = provider.GetRequiredService<CloudJobRunner>();
        runner.OnProgress.ShouldNotBeNull("nothing in the app listened to the cloud runner's progress");
        await runner.UploadInputsAsync(request, TestContext.Current.CancellationToken);
        await gcp.CreateVmAsync(request.Spec, zone, TestContext.Current.CancellationToken);
        await provider.GetRequiredService<IRunRepository>().UpsertAsync(
            new RunRecord(
                jobId,
                JobPhase.Running,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                Target: "cloud",
                OptionsJson: RunOptionsJson.Serialize(options),
                ProjectId: project,
                Bucket: FakeGcp.BucketName(project),
                VmName: request.Spec.VmName,
                AppVersion: "0.1.0",
                InstallationId: "install-1"),
            TestContext.Current.CancellationToken);
        var page = provider.GetRequiredService<RunProgressViewModel>();
        page.JobId = jobId;
        page.StatusDetailText.ShouldBeNullOrEmpty("nothing has been published yet");
        var expected = provider.GetRequiredService<IStringResourceProvider>().GetString("ProgressModelLoading");
        expected.ShouldNotBe("ProgressModelLoading", "the resw line must resolve, or this test proves nothing");

        var launch = provider.GetRequiredService<JobReconciler>().ReattachAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (page.StatusDetailText != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        var seen = page.StatusDetailText;
        await provider.GetRequiredService<JobEngine>().CancelRunAsync(jobId, TestContext.Current.CancellationToken);
        await launch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        seen.ShouldBe(expected);
        seen!.ShouldNotContain("model-loading");
    }
}
