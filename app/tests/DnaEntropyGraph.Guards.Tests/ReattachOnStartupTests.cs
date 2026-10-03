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
/// Issue #59's observable, through the production wiring: a run row a killed app left in a non-terminal phase is driven to a
/// terminal one by the entry the app itself calls at launch (<see cref="AppStartup.BeginAsync"/>), with every service resolved
/// from the real container and a real SQLite file. A reconciler that is registered but never invoked passes every unit test and
/// fails here.
/// </summary>
public class ReattachOnStartupTests : IDisposable
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-reattach-startup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may still hold the file for a moment; the temp folder is not worth failing a test over.
            }
        }
    }

    private ServiceProvider Build(bool connected)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root);
        if (connected)
        {
            // The last registration wins: the same fake, but connected, behind every resilient wrapper the app resolves.
            services.AddSingleton<FakeGcp>(_ => new FakeGcp());
        }

        return services.BuildServiceProvider();
    }

    private async Task<string> SeedKilledRunAsync(ServiceProvider provider, string jobId, JobPhase phase, bool vm)
    {
        // Runs.ProjectId references Projects: the setup wizard writes that row before any run exists.
        using (var connection = provider.GetRequiredService<DnaEntropyGraph.Persistence.SqliteDatabase>().OpenConnection())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO Projects (ProjectId) VALUES ($id)";
            insert.Parameters.AddWithValue("$id", Project);
            insert.ExecuteNonQuery();
        }

        var inputs = provider.GetRequiredService<IRunInputStore>();
        var original = Path.Combine(_root, "user", "seq.gb");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "LOCUS       seq\nORIGIN\n        1 acgtacgtac\n//\n");
        var copy = await inputs.StageAsync(jobId, original, CancellationToken.None);
        var output = Path.Combine(_root, "out");
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", InputPath = original, OutputFolder = output };
        var request = CloudJobRequestFactory.Create(options, jobId, Project, "install-1", "0.1.0", null, [copy], output);
        if (vm)
        {
            var gcp = provider.GetRequiredService<FakeGcp>();
            await provider.GetRequiredService<CloudJobRunner>().UploadInputsAsync(request, CancellationToken.None);
            await gcp.CreateVmAsync(request.Spec, Zone, CancellationToken.None);
        }

        await provider.GetRequiredService<IRunRepository>().UpsertAsync(
            new RunRecord(
                jobId,
                phase,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                Target: "cloud",
                OptionsJson: RunOptionsJson.Serialize(options),
                ProjectId: Project,
                VmName: request.Spec.VmName,
                AppVersion: "0.1.0",
                InstallationId: "install-1"),
            CancellationToken.None);
        return jobId;
    }

    private static async Task<RunRecord> RowAsync(ServiceProvider provider, string jobId)
        => (await provider.GetRequiredService<IRunRepository>().GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);

    [Fact]
    public void The_reconciler_resolves_from_the_production_container()
    {
        using var provider = Build(connected: true);

        provider.GetRequiredService<JobReconciler>().ShouldNotBeNull();
    }

    [Fact]
    public async Task The_launch_entry_drives_a_run_left_Running_to_Completed()
    {
        using var provider = Build(connected: true);
        await SeedKilledRunAsync(provider, "job-launch", JobPhase.Running, vm: true);
        (await RowAsync(provider, "job-launch")).Phase.ShouldBe(JobPhase.Running, "precondition: the row is non-terminal before launch");

        await AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken);

        var row = await RowAsync(provider, "job-launch");
        row.Phase.ShouldBe(JobPhase.Completed, row.ErrorCode);
        row.OutputDir.ShouldNotBeNull();
        Directory.EnumerateFiles(row.OutputDir, "*", SearchOption.AllDirectories).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_launch_entry_with_the_production_not_connected_cloud_does_not_throw_and_does_not_fail_a_run_it_cannot_judge()
    {
        using var provider = Build(connected: false);
        await SeedKilledRunAsync(provider, "job-offline", JobPhase.Running, vm: false);

        await Should.NotThrowAsync(() => AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken));

        var row = await RowAsync(provider, "job-offline");
        row.Phase.ShouldBe(JobPhase.Running);
        row.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void OnLaunched_calls_the_startup_entry_the_tests_above_drive()
    {
        // The tests above call AppStartup.BeginAsync directly; only this proves the app's own launch does too (a removed line in
        // OnLaunched would leave every one of them green, and the reconciler registered and never run).
        var launched = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "App.xaml.cs"));

        launched.ShouldContain("AppStartup.BeginAsync(Services", Case.Sensitive, "App.OnLaunched must start the reconciler through AppStartup.BeginAsync");
    }
}
