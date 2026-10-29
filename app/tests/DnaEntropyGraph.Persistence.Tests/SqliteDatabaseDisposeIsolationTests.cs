using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issue #741: SqliteDatabase.Dispose used to call the process-wide SqliteConnection.ClearAllPools(), so disposing one
/// database (a finished test's DI container, or any second instance) closed the pooled handles of every other database
/// in the process while they were being rented, surfacing as ObjectDisposedException from SafeHandle.DangerousAddRef.
/// </summary>
public class SqliteDatabaseDisposeIsolationTests
{
    [Fact]
    public async Task Disposing_one_database_never_disturbs_connections_opened_on_another()
    {
        using var live = new TempPaths();
        using var other = new TempPaths();
        var keep = new SqliteDatabase(live.DatabasePath);
        keep.OpenConnection().Dispose();
        var stop = false;
        Exception? failure = null;

        var churn = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                new SqliteDatabase(other.DatabasePath).Dispose();
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            for (var i = 0; i < 20000 && failure is null; i++)
            {
                try
                {
                    using var connection = keep.OpenConnection();
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA user_version;";
                    command.ExecuteScalar();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await churn;
        }

        failure.ShouldBeNull();
    }
}
