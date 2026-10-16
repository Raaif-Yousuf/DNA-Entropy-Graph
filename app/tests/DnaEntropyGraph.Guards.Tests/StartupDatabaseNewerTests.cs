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
        SqliteConnection.ClearAllPools();
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
        using (var seed = new SqliteConnection($"Data Source={root.DatabaseFile}"))
        {
            seed.Open();
            using var set = seed.CreateCommand();
            set.CommandText = "CREATE TABLE Marker (X INTEGER); PRAGMA user_version = 99;";
            set.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("DatabaseNewerThanApp").Returns("newer {0} app {1}");
        string? shown = null;

        StartupDataRoot.TryOpenDatabase(root, m => shown = m, strings).ShouldBeFalse();

        shown.ShouldBe("newer 99 app 1");
        File.Exists(root.DatabaseFile).ShouldBeTrue();
        Directory.GetFiles(Path.GetDirectoryName(root.DatabaseFile)!, "*.corrupt-*").ShouldBeEmpty();
    }

    [Fact]
    public void A_fresh_folder_opens_and_shows_nothing()
    {
        var root = DnaEntropyGraph.Core.AppDataRoot.FromPath(_dir);

        StartupDataRoot.TryOpenDatabase(root, _ => throw new InvalidOperationException("must not show"), Substitute.For<IStringResourceProvider>()).ShouldBeTrue();
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
