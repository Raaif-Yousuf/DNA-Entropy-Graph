using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issue #842 cold review: the start-up probe is read-only, never migrates, never moves a file, and a database
/// that is merely busy is never mistaken for a corrupt one.
/// </summary>
public class DatabaseStartupCheckTests
{
    private static void Seed(string path, string sql)
    {
        using var seed = new SqliteConnection($"Data Source={path};Pooling=False");
        seed.Open();
        using var command = seed.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_missing_file_passes_and_is_not_created()
    {
        using var paths = new TempPaths();

        DatabaseStartupCheck.Run(paths.DatabasePath);

        File.Exists(paths.DatabasePath).ShouldBeFalse();
    }

    [Fact]
    public void An_older_or_current_database_passes_and_is_not_migrated()
    {
        using var paths = new TempPaths();
        Seed(paths.DatabasePath, "CREATE TABLE Marker (X INTEGER); PRAGMA user_version = 0;");

        DatabaseStartupCheck.Run(paths.DatabasePath);

        using var check = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly;Pooling=False");
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        Convert.ToInt64(read.ExecuteScalar()).ShouldBe(0L);
    }

    [Fact]
    public void A_newer_database_throws_the_dedicated_exception()
    {
        using var paths = new TempPaths();
        Seed(paths.DatabasePath, "PRAGMA user_version = 99;");

        var ex = Should.Throw<DatabaseNewerThanAppException>(() => DatabaseStartupCheck.Run(paths.DatabasePath));

        ex.DatabaseVersion.ShouldBe(99L);
        ex.AppVersion.ShouldBe(DatabaseStartupCheck.LatestSchemaVersion);
    }

    [Fact]
    public void A_corrupt_file_passes_so_the_normal_open_can_quarantine_it()
    {
        using var paths = new TempPaths();
        File.WriteAllBytes(paths.DatabasePath, "this is not a sqlite file"u8.ToArray());

        DatabaseStartupCheck.Run(paths.DatabasePath);

        File.Exists(paths.DatabasePath).ShouldBeTrue();
    }

    [Fact]
    public void A_database_locked_by_another_connection_throws_in_use_and_is_untouched()
    {
        using var paths = new TempPaths();
        Seed(paths.DatabasePath, "CREATE TABLE Marker (X INTEGER); PRAGMA user_version = 1;");
        using var holder = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }

        Should.Throw<DatabaseInUseException>(() => DatabaseStartupCheck.Run(paths.DatabasePath));

        File.Exists(paths.DatabasePath).ShouldBeTrue();
        Directory.GetFiles(paths.Directory, "*.corrupt-*").ShouldBeEmpty();
    }

    /// <summary>The live database, locked by another copy of the app, must never be renamed or deleted as "corrupt".</summary>
    [Fact]
    public void A_busy_database_is_never_quarantined_by_the_normal_open()
    {
        using var paths = new TempPaths();
        Seed(paths.DatabasePath, "CREATE TABLE Marker (X INTEGER); PRAGMA user_version = 1;");
        using var holder = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }

        var database = new SqliteDatabase(paths.DatabasePath, portable: true);

        Should.Throw<DatabaseInUseException>(() => database.OpenConnection());

        database.RecoveredFromCorruptFileAt.ShouldBeNull();
        File.Exists(paths.DatabasePath).ShouldBeTrue();
        Directory.GetFiles(paths.Directory, "*.corrupt-*").ShouldBeEmpty();
    }

    /// <summary>If the corrupt file cannot be moved aside, it stays where it is: the app refuses rather than deleting it.</summary>
    [Fact]
    public void A_file_that_cannot_be_moved_aside_is_never_deleted()
    {
        using var paths = new TempPaths();
        File.WriteAllBytes(paths.DatabasePath, "this is not a sqlite file"u8.ToArray());
        using var hold = new FileStream(paths.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var database = new SqliteDatabase(paths.DatabasePath);

        Should.Throw<DatabaseInUseException>(() => database.OpenConnection());

        File.Exists(paths.DatabasePath).ShouldBeTrue();
        database.RecoveredFromCorruptFileAt.ShouldBeNull();
    }
}
