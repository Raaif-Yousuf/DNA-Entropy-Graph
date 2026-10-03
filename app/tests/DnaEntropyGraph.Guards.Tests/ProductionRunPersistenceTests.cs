using DnaEntropyGraph.App;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #532: <c>Runs.ProjectId REFERENCES Projects(ProjectId)</c> with foreign keys ON, and nothing
/// in production wrote a <c>Projects</c> row, so the write-ahead run upsert threw SqliteException 19
/// on the first real run. Every other test used an in-memory run repository, which has no foreign
/// keys. This one goes through the production container and a real SQLite file.
/// </summary>
public class ProductionRunPersistenceTests
{
    private const string NeverSeenProject = "never-seen-project";

    private static readonly string Pinned = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:" + new string('b', 64);

    [Fact]
    public async Task Starting_a_run_for_a_never_seen_project_persists_the_run_and_its_project_row()
    {
        var root = Path.Combine(Path.GetTempPath(), "deg-prodrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: root);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<FakeGcp>().WithSelectedProject(NeverSeenProject);
        var settings = provider.GetRequiredService<ISettingsStore>();
        settings.SetString(PinnedWorkerImageProvider.DeveloperModeSettingsKey, "true");
        settings.SetString(PinnedWorkerImageProvider.WorkerImageOverrideSettingsKey, Pinned);
        var input = Path.Combine(root, "input.gb");
        await File.WriteAllTextAsync(input, "LOCUS       Seq\nORIGIN\n        1 acgtacgtac\n//\n", TestContext.Current.CancellationToken);

        var jobId = await provider.GetRequiredService<IJobEngine>().StartRunAsync(
            new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", InputPath = input, OutputFolder = Path.Combine(root, "out") },
            TestContext.Current.CancellationToken);

        var runs = provider.GetRequiredService<IRunRepository>();
        var row = (await runs.GetAllAsync(TestContext.Current.CancellationToken)).Single(r => r.JobId == jobId);
        row.ProjectId.ShouldBe(NeverSeenProject, "the observable: the run row exists with its project id");
        var projects = await new ProjectRepository(provider.GetRequiredService<SqliteDatabase>()).GetAllAsync(TestContext.Current.CancellationToken);
        projects.Select(p => p.ProjectId).ShouldBe([NeverSeenProject], "and so does the Projects row it references");

        // Let the background runner finish before the temp folder is left behind.
        for (var i = 0; i < 200; i++)
        {
            var phase = (await runs.GetAllAsync(TestContext.Current.CancellationToken)).Single(r => r.JobId == jobId).Phase;
            if (JobStateMachine.IsTerminal(phase))
            {
                break;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
