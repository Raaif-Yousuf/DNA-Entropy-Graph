using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>Upgrading a file that is already at an older schema through the real embedded migrations, in order (issues #91 and #109).</summary>
public class MigrationUpgradeTests
{
    private static SqliteConnection Open(TempPaths paths)
    {
        var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_file_at_0001_upgrades_through_0002_then_0003_in_order_keeping_its_rows()
    {
        var all = SqliteMigrations.LoadEmbedded();
        all.Select(m => m.Version).ShouldBe(all.Select(m => m.Version).Order(), "the migrations are applied in version order");
        all.Select(m => m.Version).Take(3).ShouldBe([1L, 2L, 3L]);

        using var paths = new TempPaths();
        using var connection = Open(paths);
        SqliteMigrations.Apply(connection, all.Where(m => m.Version == 1).ToList());
        Scalar(connection, "PRAGMA user_version;").ShouldBe(1L);
        Exec(connection, "INSERT INTO Runs (Id, Name, Target, Phase, CreatedAt, OptionsJson) VALUES ('job-a', 'job-a', 'cloud', 'Completed', '2026-09-19T00:00:00Z', '{}');");
        Exec(connection, "INSERT INTO CostLedger (RunId, Kind, StartedUtc, UsdEst) VALUES ('job-a', 'vm_runtime', '2026-10-01T00:00:00Z', 1.0);");

        SqliteMigrations.Apply(connection, all);

        Scalar(connection, "PRAGMA user_version;").ShouldBe(all.Max(m => m.Version));
        Scalar(connection, "SELECT PreemptionCount FROM Runs WHERE Id = 'job-a';").ShouldBe(0L, "0002 ran");
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_CostLedger_RunId_Kind';").ShouldBe(1L, "0003 ran");
        Scalar(connection, "SELECT COUNT(*) FROM CostLedger;").ShouldBe(1L);
    }

    [Fact]
    public void A_file_with_duplicate_ledger_rows_still_upgrades_keeping_the_first_of_each_run_and_kind()
    {
        var all = SqliteMigrations.LoadEmbedded();
        using var paths = new TempPaths();
        using var connection = Open(paths);
        SqliteMigrations.Apply(connection, all.Where(m => m.Version <= 2).ToList());
        Exec(connection, "INSERT INTO CostLedger (RunId, Kind, StartedUtc, UsdEst) VALUES ('job-a', 'vm_runtime', '2026-10-01T00:00:00Z', 1.0);");
        Exec(connection, "INSERT INTO CostLedger (RunId, Kind, StartedUtc, UsdEst) VALUES ('job-a', 'vm_runtime', '2026-10-01T00:00:00Z', 9.0);");
        Exec(connection, "INSERT INTO CostLedger (RunId, Kind, StartedUtc, UsdEst) VALUES ('job-a', 'storage', '2026-10-01T00:00:00Z', 2.0);");
        Exec(connection, "INSERT INTO CostLedger (RunId, Kind, StartedUtc, UsdEst) VALUES (NULL, 'storage', '2026-10-01T00:00:00Z', 3.0);");
        Exec(connection, "INSERT INTO CostLedger (RunId, Kind, StartedUtc, UsdEst) VALUES (NULL, 'storage', '2026-10-01T00:00:00Z', 3.0);");

        SqliteMigrations.Apply(connection, all);

        Scalar(connection, "PRAGMA user_version;").ShouldBe(all.Max(m => m.Version));
        Scalar(connection, "SELECT COUNT(*) FROM CostLedger WHERE RunId = 'job-a' AND Kind = 'vm_runtime';").ShouldBe(1L);
        Scalar(connection, "SELECT CAST(UsdEst AS INTEGER) FROM CostLedger WHERE RunId = 'job-a' AND Kind = 'vm_runtime';").ShouldBe(1L, "the first row (lowest Id) is kept");
        Scalar(connection, "SELECT COUNT(*) FROM CostLedger WHERE RunId = 'job-a' AND Kind = 'storage';").ShouldBe(1L);
        Scalar(connection, "SELECT COUNT(*) FROM CostLedger WHERE RunId IS NULL;").ShouldBe(2L, "rows with no run are never touched");
    }
}
