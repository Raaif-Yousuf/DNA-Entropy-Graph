using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #842: a run history written by a newer app ends the launch with a plain message that names one action,
/// and the user's file is never moved aside as "corrupt".
/// </summary>
public sealed class StartupDatabaseNewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-842-" + Guid.NewGuid().ToString("N"));

    public StartupDatabaseNewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort temp cleanup.
        }
    }

    [Fact]
    public void A_newer_database_stops_startup_with_the_message_and_is_left_in_place()
    {
        var root = DnaEntropyGraph.Core.AppDataRoot.FromPath(_dir);
        using (var seed = new SqliteConnection($"Data Source={root.DatabaseFile};Pooling=False"))
        {
            seed.Open();
            using var set = seed.CreateCommand();
            set.CommandText = "CREATE TABLE Marker (X INTEGER); PRAGMA user_version = 99;";
            set.ExecuteNonQuery();
        }

        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("DatabaseNewerThanApp").Returns("newer {0} app {1}");
        string? shown = null;

        StartupDataRoot.CheckDatabase(root, m => shown = m, strings).ShouldBe(StartupExitCodes.DatabaseNewerThanApp);

        shown.ShouldBe($"newer 99 app {DnaEntropyGraph.Persistence.DatabaseStartupCheck.LatestSchemaVersion}");
        File.Exists(root.DatabaseFile).ShouldBeTrue();
        Directory.GetFiles(Path.GetDirectoryName(root.DatabaseFile)!, "*.corrupt-*").ShouldBeEmpty();
    }

    [Fact]
    public void A_fresh_folder_opens_and_shows_nothing()
    {
        var root = DnaEntropyGraph.Core.AppDataRoot.FromPath(_dir);

        StartupDataRoot.CheckDatabase(root, _ => throw new InvalidOperationException("must not show"), Substitute.For<IStringResourceProvider>())
            .ShouldBe(StartupExitCodes.Ok);
        File.Exists(root.DatabaseFile).ShouldBeFalse();
    }

    [Fact]
    public void A_database_in_use_maps_to_its_own_exit_code_and_message()
    {
        var root = DnaEntropyGraph.Core.AppDataRoot.FromPath(_dir);
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("DatabaseInUse").Returns("in use");
        string? shown = null;

        StartupDataRoot.CheckDatabase(root, m => shown = m, strings, _ => throw new DnaEntropyGraph.Persistence.DatabaseInUseException("x"))
            .ShouldBe(StartupExitCodes.DatabaseInUse);

        shown.ShouldBe("in use");
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public void Any_other_probe_failure_shows_a_message_and_never_crashes(Type exceptionType)
    {
        var root = DnaEntropyGraph.Core.AppDataRoot.FromPath(_dir);
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("DatabaseUnusable").Returns("unusable {0}");
        string? shown = null;

        StartupDataRoot.CheckDatabase(root, m => shown = m, strings, _ => throw (Exception)Activator.CreateInstance(exceptionType)!)
            .ShouldBe(StartupExitCodes.DatabaseUnusable);

        shown.ShouldBe($"unusable {root.Path}");
    }

    [Fact]
    public void The_exit_codes_are_distinct()
    {
        new[] { StartupExitCodes.Ok, StartupExitCodes.DataFolderUnusable, StartupExitCodes.DatabaseNewerThanApp, StartupExitCodes.DatabaseInUse, StartupExitCodes.DatabaseUnusable }
            .Distinct().Count().ShouldBe(5);
    }

    [Fact]
    public void The_in_use_and_unusable_messages_name_one_action()
    {
        var resw = string.Concat(RepoPaths.AllReswFiles.Select(File.ReadAllText));
        foreach (var (key, action) in new[] { ("DatabaseInUse", "Close the other copy"), ("DatabaseUnusable", "not read-only") })
        {
            var start = resw.IndexOf($"name=\"{key}\"", StringComparison.Ordinal);
            start.ShouldBeGreaterThan(0, key);
            resw.Substring(start, resw.IndexOf("</data>", start, StringComparison.Ordinal) - start).ShouldContain(action);
        }
    }

    [Fact]
    public void The_message_names_the_one_action()
    {
        var resw = string.Concat(RepoPaths.AllReswFiles.Select(File.ReadAllText));
        var start = resw.IndexOf("name=\"DatabaseNewerThanApp\"", StringComparison.Ordinal);

        start.ShouldBeGreaterThan(0);
        resw.Substring(start, resw.IndexOf("</data>", start, StringComparison.Ordinal) - start)
            .ShouldContain("Install the latest version of DNA Entropy Graph.");
    }
}
