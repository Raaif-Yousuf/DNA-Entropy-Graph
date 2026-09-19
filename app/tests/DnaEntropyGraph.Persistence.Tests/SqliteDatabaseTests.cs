using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class SqliteDatabaseTests
{
    /// <summary>
    /// Guards.Tests/DiResolutionTests constructs every registered service
    /// purely to check the DI graph is wireable - it never calls a
    /// repository method. If the constructor touched disk, that guard test
    /// would create a real directory (or a real database) under
    /// %LOCALAPPDATA% on every developer machine and on CI as a side effect
    /// of a test that has nothing to do with SQLite, and a failure there
    /// would misleadingly read as a DI bug rather than a filesystem one.
    /// </summary>
    [Fact]
    public void Constructing_creates_nothing_on_disk_only_OpenConnection_does()
    {
        using var paths = new TempPaths();
        var nestedPath = Path.Combine(paths.Directory, "nested", "does-not-exist-yet", "app.db");

        var database = new SqliteDatabase(nestedPath);

        File.Exists(nestedPath).ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(nestedPath)).ShouldBeFalse();

        using var connection = database.OpenConnection();

        File.Exists(nestedPath).ShouldBeTrue();
    }

    [Fact]
    public void A_fresh_nonexistent_file_is_migrated_to_the_latest_schema()
    {
        using var paths = new TempPaths();
        var database = new SqliteDatabase(paths.DatabasePath);

        using var connection = database.OpenConnection();

        GetUserVersion(connection).ShouldBe(1L);
        TableExists(connection, "Runs").ShouldBeTrue();
        TableExists(connection, "CloudResources").ShouldBeTrue();
        TableExists(connection, "Projects").ShouldBeTrue();
        TableExists(connection, "CostLedger").ShouldBeTrue();
    }

    [Fact]
    public async Task Reopening_an_already_migrated_populated_file_does_not_re_run_migrations_or_lose_data()
    {
        using var paths = new TempPaths();
        var firstLaunch = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await firstLaunch.UpsertAsync(
            new DnaEntropyGraph.Core.Abstractions.RunRecord("job-x", DnaEntropyGraph.Core.JobPhase.Running, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var secondLaunch = new SqliteDatabase(paths.DatabasePath);
        using var connection = secondLaunch.OpenConnection();

        GetUserVersion(connection).ShouldBe(1L);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Runs WHERE Id = 'job-x';";
        Convert.ToInt64(command.ExecuteScalar()).ShouldBe(1L);
    }

    /// <summary>
    /// Issue #67's "migrations that run forward from an empty file AND from
    /// a 0001 file, both tested": this exercises the runner against a
    /// synthetic second migration on a database that already has 0001
    /// applied and a real row in it, proving the forward-migration path
    /// without waiting for a real 0002_*.sql to exist.
    /// </summary>
    [Fact]
    public void Applying_a_second_migration_against_a_populated_database_adds_the_column_without_touching_existing_rows()
    {
        using var paths = new TempPaths();
        var database = new SqliteDatabase(paths.DatabasePath);
        using var connection = database.OpenConnection(); // applies 0001
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO Runs (Id, Name, Target, Phase, CreatedAt, OptionsJson) VALUES ('job-y', 'job-y', 'cloud', 'Running', '2026-09-19T00:00:00Z', '{}');";
            insert.ExecuteNonQuery();
        }

        var syntheticMigrations = new[]
        {
            (Version: 1L, Sql: "SELECT 1;"), // already applied, must be skipped, not re-run
            (Version: 2L, Sql: "ALTER TABLE Runs ADD COLUMN TestColumn TEXT;"),
        };

        SqliteMigrations.Apply(connection, syntheticMigrations);

        GetUserVersion(connection).ShouldBe(2L);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT TestColumn FROM Runs WHERE Id = 'job-y';";
            check.ExecuteScalar().ShouldBeOfType<DBNull>();
        }
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM Runs;";
            Convert.ToInt64(count.ExecuteScalar()).ShouldBe(1L);
        }
    }

    [Fact]
    public void A_corrupt_database_file_is_quarantined_and_replaced_with_a_fresh_migrated_one()
    {
        using var paths = new TempPaths();
        File.WriteAllBytes(paths.DatabasePath, "this is not a sqlite file"u8.ToArray());
        var database = new SqliteDatabase(paths.DatabasePath);

        using var connection = database.OpenConnection();

        GetUserVersion(connection).ShouldBe(1L);
        TableExists(connection, "Runs").ShouldBeTrue();
        database.RecoveredFromCorruptFileAt.ShouldNotBeNull();
        File.Exists(database.RecoveredFromCorruptFileAt!).ShouldBeTrue();
        File.ReadAllText(database.RecoveredFromCorruptFileAt!).ShouldBe("this is not a sqlite file");
    }

    [Fact]
    public void The_real_embedded_migration_0001_creates_the_Runs_table()
    {
        var migrations = SqliteMigrations.LoadEmbedded();

        migrations.ShouldNotBeEmpty();
        migrations[0].Version.ShouldBe(1L);
        migrations[0].Sql.ShouldContain("CREATE TABLE Runs");
    }

    private static long GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) == 1L;
    }
}
