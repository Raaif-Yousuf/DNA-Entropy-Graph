using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.LocalEngine;
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
        while (page.StatusDetailText != "Window 3 of 10" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        var seenText = page.StatusDetailText;
        var seenFraction = page.FractionComplete;
        worker.End(1);
        await run;

        seenText.ShouldBe("Window 3 of 10");
        seenFraction.ShouldBe(0.4, 1e-9);
    }

    [Fact]
    public async Task The_production_cloud_runner_publishes_a_progress_event_to_the_run_page()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<CloudJobRunner>();
        var page = provider.GetRequiredService<RunProgressViewModel>();
        page.JobId = "job-cloud-1";

        runner.OnProgress.ShouldNotBeNull("nothing in the app listened to the cloud runner's progress");
        runner.OnProgress!("job-cloud-1", new ProgressEvent("running", 0.5, "Window 5 of 10"));

        page.StatusDetailText.ShouldBe("Window 5 of 10");
        page.FractionComplete.ShouldBe(0.5);
    }
}
