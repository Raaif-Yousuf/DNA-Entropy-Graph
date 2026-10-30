using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issue #830 stage 3: a run row remembers the Google account it was made as (<c>Runs.AccountSub</c>), so a later stop, delete or reconcile acts as that account whoever is signed in
/// as current. A row from before the column existed is backfilled from the account recorded for its project; one with no recorded account stays null, meaning the current account.
/// </summary>
public sealed class RunAccountSubTests
{
    [Fact]
    public async Task The_account_a_run_was_made_as_survives_a_relaunch()
    {
        using var paths = new TempPaths();
        var first = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await first.UpsertAsync(new RunRecord("job-a", JobPhase.Running, DateTimeOffset.UtcNow, ProjectId: null, AccountSub: "sub-a"), CancellationToken.None);

        var second = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        var row = (await second.GetAllAsync(CancellationToken.None)).Single();

        row.AccountSub.ShouldBe("sub-a");
    }

    [Fact]
    public async Task A_later_write_from_a_record_without_the_account_never_erases_it()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new RunRecord("job-b", JobPhase.Running, DateTimeOffset.UtcNow, AccountSub: "sub-b"), CancellationToken.None);

        await repository.UpsertAsync(new RunRecord("job-b", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.Phase.ShouldBe(JobPhase.Completed);
        row.AccountSub.ShouldBe("sub-b");
    }

    [Fact]
    public async Task A_database_from_before_the_column_is_backfilled_from_the_account_recorded_for_the_projects()
    {
        using var paths = new TempPaths();
        await using (var connection = new SqliteConnection("Data Source=" + paths.DatabasePath))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            foreach (var migration in SqliteMigrations.LoadEmbedded().Where(m => m.Version <= 2))
            {
                Exec(connection, migration.Sql);
            }

            Exec(connection, "PRAGMA user_version = 2;");
            Exec(connection, "INSERT INTO Accounts (Sub) VALUES ('sub-old');");
            Exec(connection, "INSERT INTO Projects (ProjectId, AccountSub) VALUES ('proj-known', 'sub-old'), ('proj-unknown', NULL);");
            foreach (var (id, project) in new[] { ("job-known", "proj-known"), ("job-unknown", "proj-unknown") })
            {
                Exec(connection, $"INSERT INTO Runs (Id, Name, Target, Phase, CreatedAt, OptionsJson, ProjectId) VALUES ('{id}', '{id}', 'cloud', 'Completed', '2026-10-01T00:00:00Z', '{{}}', '{project}');");
            }
        }

        var rows = await new RunRepository(new SqliteDatabase(paths.DatabasePath)).GetAllAsync(CancellationToken.None);

        rows.Single(r => r.JobId == "job-known").AccountSub.ShouldBe("sub-old");
        rows.Single(r => r.JobId == "job-unknown").AccountSub.ShouldBeNull("no recorded account: it stays the current account, never a guessed one");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}