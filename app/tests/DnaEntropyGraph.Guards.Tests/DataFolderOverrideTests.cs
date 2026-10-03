using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #638: with a data folder override set, every piece of app state lands under it and none under the real
/// %LOCALAPPDATA%\DNAEntropyGraph. Two halves: the real DI graph is built over an override root and every path-owning service
/// is made to write (a path nobody wired to the root would show up as a missing file under it), and a source scan proves
/// nothing but <c>AppDataRoot</c> asks Windows for the local-data folder.
/// </summary>
public sealed class DataFolderOverrideTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "deg-override-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private ServiceProvider Build(AppDataRoot root)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(root);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Every_path_owning_service_writes_under_the_override_and_none_under_the_default()
    {
        var root = AppDataRoot.Resolve(["--profile", _scratch], _ => null, Path.GetTempPath());
        var defaultRoot = AppDataRoot.Default().Path;
        root.IsOverride.ShouldBeTrue();
        await using var provider = Build(root);

        // settings.json and installation_id (the id is written on first read).
        var settings = provider.GetRequiredService<ISettingsStore>();
        settings.SetString("probe", "1");
        _ = DnaEntropyGraph.Core.Cloud.InstallationId.GetOrCreate(settings);

        // app.db.
        using (provider.GetRequiredService<DnaEntropyGraph.Persistence.SqliteDatabase>().OpenConnection())
        {
        }

        // logs\app.log.
        provider.GetRequiredService<IDiagnosticsLog>().Warning("probe", null, "other");

        // The saved input copy and the pasted-sequence copy.
        var source = Path.Combine(_scratch, "src.fa");
        await File.WriteAllTextAsync(source, ">s\nACGT\n", TestContext.Current.CancellationToken);
        var staged = await provider.GetRequiredService<IRunInputStore>().StageAsync("job1", source, TestContext.Current.CancellationToken);
        staged.LocalPath.ShouldStartWith(root.RunsDirectory);
        provider.GetRequiredService<IPastedInputStore>().Save("ACGT").ShouldStartWith(root.Path);

        // runs\<job>\logs\worker.log, read back through the log tail reader.
        var workerLog = Path.Combine(root.RunsDirectory, "job3", "logs", "worker.log");
        Directory.CreateDirectory(Path.GetDirectoryName(workerLog)!);
        await File.WriteAllTextAsync(workerLog, "hello\n", TestContext.Current.CancellationToken);
        provider.GetRequiredService<ILogTailReader>().ReadLines("job3").ShouldBe(["hello"]);

        // The diagnostics source lists what is under the override (the log written above).
        var ownFiles = new FolderDiagnosticsSource(root.Path).ListFiles();
        ownFiles.ShouldContain(f => f.EndsWith("app.log", StringComparison.Ordinal));

        File.Exists(root.SettingsFile).ShouldBeTrue();
        File.Exists(root.InstallationIdFile).ShouldBeTrue();
        File.Exists(root.DatabaseFile).ShouldBeTrue();
        File.Exists(Path.Combine(root.LogsDirectory, "app.log")).ShouldBeTrue();

        // Account and viewer paths come straight from the root (no side effect to observe without a sign-in or a window).
        provider.GetRequiredService<DnaEntropyGraph.Cloud.Auth.GoogleAccountOptions>().AuthDirectory.ShouldBe(root.AuthDirectory);
        foreach (var path in new[] { root.AuthDirectory, root.WebView2Directory, root.SettingsFile, root.InstallationIdFile, root.DatabaseFile, root.LogsDirectory, root.RunsDirectory })
        {
            path.ShouldNotStartWith(defaultRoot);
        }
    }

    private static string[] FilesUsingTheLocalDataFolder(IEnumerable<string> files)
        => files
            .Where(p => !Path.GetFileName(p).Equals("AppDataRoot.cs", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void Only_AppDataRoot_asks_Windows_for_the_local_data_folder()
    {
        FilesUsingTheLocalDataFolder(RepoPaths.AllCSharpFiles)
            .ShouldBeEmpty("app state must hang off the one AppDataRoot, or --profile / DEG_DATA_DIR silently misses it (#638).");

        RepoPaths.AllCSharpFiles.Where(p => Path.GetFileName(p) == "AppDataRoot.cs")
            .Any(p => File.ReadAllText(p).Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
            .ShouldBeTrue("vacuity: the scan must see the one allowed site.");
    }

    [Fact]
    public void The_scan_catches_a_planted_use()
    {
        Directory.CreateDirectory(_scratch);
        var planted = Path.Combine(_scratch, "Planted.cs");
        File.WriteAllText(planted, "var x = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);");

        FilesUsingTheLocalDataFolder([planted]).ShouldBe([planted]);
    }
}
