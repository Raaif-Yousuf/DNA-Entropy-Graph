using CommunityToolkit.Mvvm.Messaging;
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
/// Issue #59 cold review: a run the reconciler reattached is driven through the same registry as a run the engine started, from the
/// first moment of the reattach (the look at the cloud included), so Cancel on it stops and awaits the driver first. Without that two
/// writers race on the row and Cancelled can be overwritten.
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

    private async Task<(ServiceProvider Provider, FakeGcp Gcp)> BuildWithRunningRowAsync(string jobId)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root);
        services.AddSingleton<FakeGcp>(_ => new FakeGcp().WithWorker(FakeWorkerMode.Never));
        var provider = services.BuildServiceProvider();
        var gcp = provider.GetRequiredService<FakeGcp>();

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
        await gcp.CreateVmAsync(request.Spec, Zone, TestContext.Current.CancellationToken);
        await provider.GetRequiredService<IRunRepository>().UpsertAsync(
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
        return (provider, gcp);
    }

    private static async Task WaitUntilActiveAsync(ActiveRuns registry, string jobId)
    {
        var waited = 0;
        while (!registry.IsActive(jobId) && waited++ < 500)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        registry.IsActive(jobId).ShouldBeTrue("the reattached run must be registered where Cancel looks");
    }

    [Fact]
    public async Task Cancel_on_a_reattached_run_ends_it_Cancelled_and_nothing_writes_after()
    {
        var jobId = "job-cancel";
        var (provider, gcp) = await BuildWithRunningRowAsync(jobId);
        using var _ = provider;
        var reconciler = provider.GetRequiredService<JobReconciler>();

        var launch = reconciler.ReattachAsync(TestContext.Current.CancellationToken);
        await WaitUntilActiveAsync(provider.GetRequiredService<ActiveRuns>(), jobId);
        await provider.GetRequiredService<JobEngine>().CancelRunAsync(jobId, TestContext.Current.CancellationToken);
        var outcomes = await launch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var row = (await provider.GetRequiredService<IRunRepository>().GetAllAsync(TestContext.Current.CancellationToken)).Single(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Cancelled, row.ErrorCode);
        row.ErrorCode.ShouldBeNull("the driver was stopped before the cancel wrote, so it recorded no failure over it");
        outcomes.Single().FinalPhase.ShouldBe(JobPhase.Cancelled, "the outcome reports how the run ended, not the phase before the cancel wrote");
        (await gcp.FindByJobIdAsync(jobId, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_while_the_reattach_is_still_looking_at_the_cloud_writes_exactly_one_Cancelling_then_Cancelled()
    {
        var jobId = "job-look";
        var (provider, gcp) = await BuildWithRunningRowAsync(jobId);
        using var _ = provider;
        var phases = new List<JobPhase>();
        var recipient = new object();
        provider.GetRequiredService<IMessenger>().Register<object, DnaEntropyGraph.Presentation.Messaging.RunPhaseChangedMessage>(recipient, (_, m) =>
        {
            if (m.JobId == jobId)
            {
                lock (phases)
                {
                    phases.Add(m.Phase);
                }
            }
        });
        gcp.WithHungCalls(1);
        var reconciler = provider.GetRequiredService<JobReconciler>();

        var launch = reconciler.ReattachAsync(TestContext.Current.CancellationToken);
        await WaitUntilActiveAsync(provider.GetRequiredService<ActiveRuns>(), jobId);
        for (var waited = 0; gcp.HungCalls == 0 && waited < 500; waited++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        gcp.HungCalls.ShouldBe(1, "precondition: the reattach is parked inside its look at the cloud");
        await provider.GetRequiredService<JobEngine>().CancelRunAsync(jobId, TestContext.Current.CancellationToken);
        var outcomes = await launch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        lock (phases)
        {
            phases.ShouldBe([JobPhase.Cancelling, JobPhase.Cancelled], "one cancel, no run started on a Cancelling row, no illegal transition");
        }

        outcomes.Single().FinalPhase.ShouldBe(JobPhase.Cancelled);
        (await provider.GetRequiredService<IRunRepository>().GetAllAsync(TestContext.Current.CancellationToken)).Single(r => r.JobId == jobId).Phase.ShouldBe(JobPhase.Cancelled);
        GC.KeepAlive(recipient);
    }
}
