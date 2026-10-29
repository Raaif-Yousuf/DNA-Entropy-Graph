using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issue #842: the embedded migration set must be exactly 1..N with
/// <c>NNNN_snake.sql</c> names, or a branch that lands 0004 before 0002 moves
/// every install past a migration that then never runs.
/// </summary>
public class SqliteMigrationSetTests
{
    private static (string FileName, string Sql) F(string name) => (name, "SELECT 1;");

    [Fact]
    public void A_contiguous_set_is_returned_in_version_order()
    {
        var set = SqliteMigrations.BuildSet([F("0002_b.sql"), F("0001_a.sql"), F("0003_c.sql")]);

        set.Select(m => m.Version).ToList().ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void A_gap_throws_and_names_the_missing_version()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => SqliteMigrations.BuildSet([F("0001_a.sql"), F("0003_c.sql")]));

        ex.Message.ShouldContain("0002");
        ex.Message.ShouldContain("gap");
    }

    [Fact]
    public void A_set_not_starting_at_one_throws()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => SqliteMigrations.BuildSet([F("0002_b.sql"), F("0003_c.sql")]));

        ex.Message.ShouldContain("0001");
    }

    [Fact]
    public void A_duplicate_number_throws_and_names_both_files()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => SqliteMigrations.BuildSet([F("0001_a.sql"), F("0002_b.sql"), F("0002_other.sql")]));

        ex.Message.ShouldContain("0002_b.sql");
        ex.Message.ShouldContain("0002_other.sql");
        ex.Message.ShouldContain("Duplicate");
    }

    [Theory]
    [InlineData("1_a.sql")]
    [InlineData("0001a.sql")]
    [InlineData("0001_Initial.sql")]
    [InlineData("0001_.sql")]
    [InlineData("initial.sql")]
    public void A_badly_named_file_throws(string name)
    {
        var ex = Should.Throw<InvalidOperationException>(() => SqliteMigrations.BuildSet([F(name)]));

        ex.Message.ShouldContain(name);
        ex.Message.ShouldContain("NNNN_snake.sql");
    }

    [Fact]
    public void The_real_embedded_set_is_contiguous_from_one()
    {
        var versions = SqliteMigrations.LoadEmbedded().Select(m => m.Version).ToList();

        versions.ShouldBe(Enumerable.Range(1, versions.Count).Select(i => (long)i).ToList());
    }

    [Fact]
    public void A_newer_database_file_is_not_quarantined_or_deleted_when_opened()
    {
        using var paths = new TempPaths();
        using (var seed = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            seed.Open();
            using var set = seed.CreateCommand();
            set.CommandText = "CREATE TABLE Marker (X INTEGER); PRAGMA user_version = 99;";
            set.ExecuteNonQuery();
        }

        var database = new SqliteDatabase(paths.DatabasePath);

        Should.Throw<DatabaseNewerThanAppException>(() => database.OpenConnection());

        database.RecoveredFromCorruptFileAt.ShouldBeNull();
        File.Exists(paths.DatabasePath).ShouldBeTrue();
        Directory.GetFiles(paths.Directory, "*.corrupt-*").ShouldBeEmpty();
        database.Dispose(); // releases this database's own pool only; ClearAllPools would break parallel tests (#741)
        using var check = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly");
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        Convert.ToInt64(read.ExecuteScalar()).ShouldBe(99L);
        read.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'Marker';";
        Convert.ToInt64(read.ExecuteScalar()).ShouldBe(1L);
    }

    [Fact]
    public void A_database_newer_than_this_app_is_refused_with_an_action_and_left_untouched()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var set = connection.CreateCommand())
        {
            set.CommandText = "PRAGMA user_version = 9; CREATE TABLE Marker (X INTEGER);";
            set.ExecuteNonQuery();
        }

        var ex = Should.Throw<DatabaseNewerThanAppException>(
            () => SqliteMigrations.Apply(connection, [(1L, "SELECT 1;"), (2L, "SELECT 1;")]));

        ex.DatabaseVersion.ShouldBe(9L);
        ex.AppVersion.ShouldBe(2L);
        ex.Message.ShouldContain("newer");
        ex.Message.ShouldContain("Install the latest version");
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        Convert.ToInt64(read.ExecuteScalar()).ShouldBe(9L);
    }
}
