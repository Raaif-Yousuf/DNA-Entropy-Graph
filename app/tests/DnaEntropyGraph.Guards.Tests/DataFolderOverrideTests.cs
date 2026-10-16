using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
            // ViewerLocator only reads the per-user "Programs" install folder to find IGV/Geneious; it is not app state (docs/hard_rules.md rule 14).
            .Where(p => !Path.GetFileName(p).Equals("AppDataRoot.cs", StringComparison.Ordinal) && !Path.GetFileName(p).Equals("ViewerLocator.cs", StringComparison.Ordinal))
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

    [Fact]
    public void Startup_resolves_against_the_real_program_directory_so_a_portable_flag_beside_the_exe_is_honoured()
    {
        // Issue #144: behavioural. A flag planted beside the test host (AppContext.BaseDirectory, what production passes) must switch the
        // resolver the app actually starts with to portable mode; if Startup stopped passing the program directory the flag is never looked at.
        Environment.GetEnvironmentVariable(AppDataRoot.EnvironmentVariable).ShouldBeNull("the test needs DEG_DATA_DIR unset");
        var flag = Path.Combine(AppContext.BaseDirectory, AppDataRoot.PortableFlagFileName);
        var data = Path.Combine(AppContext.BaseDirectory, AppDataRoot.PortableDataFolderName);
        File.Exists(flag).ShouldBeFalse();
        Directory.Exists(data).ShouldBeFalse();
        try
        {
            File.WriteAllText(flag, string.Empty);

            var root = StartupDataRoot.ResolveForThisProcess([]);

            root.IsPortable.ShouldBeTrue();
            root.Path.ShouldBe(data);
        }
        finally
        {
            File.Delete(flag);
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(DataFolderProblem.CannotCreate, "DataFolderUnusable_Portable_CannotCreate")]
    [InlineData(DataFolderProblem.NotAFolder, "DataFolderUnusable_Portable_NotAFolder")]
    public void A_portable_failure_uses_the_portable_copy_and_an_override_failure_keeps_the_profile_copy(DataFolderProblem problem, string portableKey)
    {
        var strings = NSubstitute.Substitute.For<IStringResourceProvider>();
        strings.GetString(NSubstitute.Arg.Any<string>()).Returns(call => call.Arg<string>() + " {0}");

        StartupDataRoot.MessageFor(new DataFolderUnusableException(problem, "P", isPortable: true), strings).ShouldBe(portableKey + " P");
        StartupDataRoot.MessageFor(new DataFolderUnusableException(problem, "P"), strings).ShouldNotContain("Portable");
    }

    [Fact]
    public void The_portable_failure_copy_names_one_action_and_never_the_profile_switch()
    {
        var resw = string.Concat(RepoPaths.AllReswFiles.Select(File.ReadAllText));
        foreach (var key in new[] { "DataFolderUnusable_Portable_CannotCreate", "DataFolderUnusable_Portable_NotAFolder" })
        {
            var start = resw.IndexOf($"name=\"{key}\"", StringComparison.Ordinal);
            start.ShouldBeGreaterThan(0, key);
            var text = resw.Substring(start, resw.IndexOf("</data>", start, StringComparison.Ordinal) - start);
            text.ShouldContain("portable.flag");
            text.ShouldNotContain("--profile");
            text.ShouldNotContain("you chose");
        }
    }
    [Fact]
    public void Only_AppDataRoot_names_the_portable_flag()
    {
        var users = RepoPaths.AllCSharpFiles
            .Where(p => File.ReadAllText(p).Contains("\"portable.flag\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        users.ShouldBe(["AppDataRoot.cs"], "one resolver decides where state lives (#144); tests use AppDataRoot.PortableFlagFileName.");
    }
}
