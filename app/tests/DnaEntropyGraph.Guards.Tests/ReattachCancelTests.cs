using DnaEntropyGraph.App;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #59 cold review: a run the reconciler reattached is driven through the same registry as a run the engine started, so
/// Cancel on it stops and awaits the driver first. Without that two writers race on the row and Cancelled can be overwritten.
/// </summary>
public class ReattachCancelTests : IDisposable
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-reattach-cancel-" + Guid.NewGuid().ToString("N"));

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
            // SQLite may still hold the file; not worth failing a test over.
        }
    }

    [Fact]
    public async Task Cancel_on_a_reattached_run_ends_it_Cancelled_and_nothing_writes_after()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root);
        services.AddSingleton<FakeGcp>(_ => new FakeGcp().WithWorker(FakeWorkerMode.Never));
        using var provider = services.BuildServiceProvider();
        var jobId = "job-cancel";

        using (var connection = provider.GetRequiredService<DnaEntropyGraph.Persistence.SqliteDatabase>().OpenConnection())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO Projects (ProjectId) VALUES ($id)";
            insert.Parameters.AddWithValue("$id", Project);
            insert.ExecuteNonQuery();
        }

        var original = Path.Combine(_root, "user", "seq.gb");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "LOCUS       seq\nORIGIN\n        1 acgtacgtac\n//\n");
        var copy = await provider.GetRequiredService<IRunInputStore>().StageAsync(jobId, original, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "out");
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", InputPath = original, OutputFolder = output };
        var request = CloudJobRequestFactory.Create(options, jobId, Project, "install-1", "0.1.0", null, [copy], output);
        await provider.GetRequiredService<CloudJobRunner>().UploadInputsAsync(request, TestContext.Current.CancellationToken);
        await provider.GetRequiredService<FakeGcp>().CreateVmAsync(request.Spec, Zone, TestContext.Current.CancellationToken);
        var runs = provider.GetRequiredService<IRunRepository>();
        await runs.UpsertAsync(
            new RunRecord(
                jobId,
                JobPhase.Running,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                Target: "cloud",
                OptionsJson: RunOptionsJson.Serialize(options),
                ProjectId: Project,
                Bucket: FakeGcp.BucketName(Project),
                VmName: request.Spec.VmName,
                AppVersion: "0.1.0",
                InstallationId: "install-1"),
            TestContext.Current.CancellationToken);

        var launch = AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken);
        var registry = provider.GetRequiredService<ActiveRuns>();
        var waited = 0;
        while (!registry.IsActive(jobId) && waited++ < 500)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        registry.IsActive(jobId).ShouldBeTrue("the reattached run must be registered where Cancel looks");

        await provider.GetRequiredService<JobEngine>().CancelRunAsync(jobId, TestContext.Current.CancellationToken);
        await launch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var row = (await runs.GetAllAsync(TestContext.Current.CancellationToken)).Single(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Cancelled, row.ErrorCode);
        row.ErrorCode.ShouldBeNull("the driver was stopped before the cancel wrote, so it recorded no failure over it");
        (await provider.GetRequiredService<FakeGcp>().FindByJobIdAsync(jobId, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}
