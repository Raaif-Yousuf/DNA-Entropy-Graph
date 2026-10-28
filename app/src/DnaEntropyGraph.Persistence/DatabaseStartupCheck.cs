using Microsoft.Data.Sqlite;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// The run history database is locked by another process (usually a second copy of the app), or a damaged file
/// could not be moved aside. The file is never renamed or deleted in either case.
/// </summary>
public sealed class DatabaseInUseException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// The cheap start-up probe (issue #842): read-only, short busy timeout, reads <c>PRAGMA user_version</c> and compares it
/// with the newest embedded migration. It never migrates, never creates a file and never moves one; the app's normal first
/// open does the migration. A file SQLite cannot read passes here so the normal open can quarantine it.
/// </summary>
public static class DatabaseStartupCheck
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    /// <summary>The newest schema version this build can open.</summary>
    public static long LatestSchemaVersion => SqliteMigrations.LoadEmbedded().Max(m => m.Version);

    /// <summary>True for SQLITE_BUSY / SQLITE_LOCKED, including their extended codes.</summary>
    internal static bool IsBusy(SqliteException ex) => (ex.SqliteErrorCode & 0xFF) is SqliteBusy or SqliteLocked;

    internal static DatabaseInUseException InUse(Exception inner) => new(
        "The run history database is in use by another copy of DNA Entropy Graph. Close the other copy, then start the app again. The file was not changed.",
        inner);

    public static void Run(string databasePath)
    {
        var latest = LatestSchemaVersion;
        if (!File.Exists(databasePath))
        {
            return;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();

        long current;
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            throw InUse(ex);
        }
        catch (SqliteException)
        {
            return; // Unreadable: the normal open decides whether it is corrupt.
        }

        if (current > latest)
        {
            throw new DatabaseNewerThanAppException(current, latest);
        }
    }
}
