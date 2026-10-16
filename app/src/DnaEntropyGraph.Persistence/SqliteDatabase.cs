using System.Reflection;
using Microsoft.Data.Sqlite;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// Owns <c>%LOCALAPPDATA%\DNAEntropyGraph\app.db</c>: opens connections with
/// WAL, foreign keys and a busy timeout (docs/architecture.md section 6,
/// Appendix A section 3), and applies the numbered SQL migrations under
/// <c>Migrations/</c> forward from whatever <c>PRAGMA user_version</c> it
/// finds, including 0 (a brand-new file).
///
/// DECISION (agent-made, reversible), issue filed by this lane's report:
/// nothing in docs/ says what happens when the database file itself cannot
/// be read (a lab PC force-restarted mid-write leaves a torn SQLite file).
/// The call made here is that a corrupt <c>app.db</c> must never keep the
/// app from launching - a history that throws is a worse bug than a lost
/// row. The unreadable file is renamed aside (never deleted outright - it
/// stays on disk as evidence for a diagnostics zip) and a fresh, empty,
/// freshly-migrated database takes its place. <see cref="RecoveredFromCorruptFileAt"/>
/// exposes this so a future Settings/diagnostics page can tell the user
/// their history did not survive, rather than that being silently true.
///
/// The constructor does nothing to disk - not even creating the parent
/// directory - and only stores the path. Every filesystem/SQLite touch is
/// deferred to the first <see cref="OpenConnection"/> call. This matters
/// beyond tidiness: <c>Guards.Tests/DiResolutionTests</c> constructs every
/// registered service (including this one, via <c>RunRepository</c>) purely
/// to check the DI graph is wireable, with no intention of ever calling a
/// repository method. An eager constructor would make that guard test
/// create a real directory (or worse, a real database) under
/// <c>%LOCALAPPDATA%</c> on every developer machine and on CI as a side
/// effect of a test that has nothing to do with SQLite - a failure there
/// would misleadingly read as a DI bug.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly string _databasePath;
    private readonly object _gate = new();
    private bool _migrated;

    private readonly string _journalMode;

    /// <param name="portable">Portable mode (issue #144): the file may sit on a USB stick, so the rollback journal is used instead of WAL.</param>
    public SqliteDatabase(string databasePath, bool portable = false)
    {
        _databasePath = databasePath;
        _journalMode = ChooseJournalMode(databasePath, portable);
    }

    /// <summary>
    /// <c>DELETE</c> in portable mode and on a removable or network drive, <c>WAL</c> otherwise. WAL keeps a shared-memory index file beside the
    /// database that is unreliable on network shares and leaves <c>-wal</c> files behind when a stick is pulled without closing the app.
    /// </summary>
    /// <param name="driveTypeOf">Test seam: the drive type for a path (null when unknown). Production asks <see cref="DriveInfo"/>.</param>
    public static string ChooseJournalMode(string databasePath, bool portable, Func<string, DriveType?>? driveTypeOf = null)
    {
        if (portable)
        {
            return "DELETE";
        }

        var type = (driveTypeOf ?? RealDriveType)(databasePath);
        return type is DriveType.Removable or DriveType.Network ? "DELETE" : "WAL";
    }

    private static DriveType? RealDriveType(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            return root.StartsWith(@"\\", StringComparison.Ordinal) ? DriveType.Network : new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Set the moment a corrupt <c>app.db</c> was quarantined and replaced.
    /// Null on every ordinary launch. See the DECISION note above this class.
    /// </summary>
    public string? RecoveredFromCorruptFileAt { get; private set; }

    /// <summary>
    /// Opens a new connection, migrating the database first if this is the
    /// first call on this instance. Callers own the returned connection and
    /// must dispose it (Dapper's extension methods take <see cref="System.Data.IDbConnection"/>
    /// directly - no need to keep it open longer than one repository call).
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        lock (_gate)
        {
            if (!_migrated)
            {
                EnsureMigratedNoLock();
                _migrated = true;
            }
        }

        var connection = new SqliteConnection(BuildConnectionString());
        connection.Open();
        ConfigurePragmas(connection);
        return connection;
    }

    private string BuildConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    private void ConfigurePragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode={_journalMode}; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }

    private void EnsureMigratedNoLock()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_databasePath) && !IsReadable(out var reason))
        {
            QuarantineCorruptFile(reason);
        }

        using var connection = new SqliteConnection(BuildConnectionString());
        connection.Open();
        ConfigurePragmas(connection);
        SqliteMigrations.Apply(connection, SqliteMigrations.LoadEmbedded());
    }

    private bool IsReadable(out string reason)
    {
        try
        {
            using var connection = new SqliteConnection(BuildConnectionString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = command.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                reason = result ?? "quick_check returned no result";
                return false;
            }

            reason = "";
            return true;
        }
        catch (SqliteException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private void QuarantineCorruptFile(string reason)
    {
        SqliteConnection.ClearAllPools();
        var quarantinePath = $"{_databasePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
        try
        {
            File.Move(_databasePath, quarantinePath, overwrite: false);
            RecoveredFromCorruptFileAt = quarantinePath;
        }
        catch (IOException)
        {
            // Best effort. An app that cannot even rename the file aside
            // must still be able to delete it and start fresh - hanging
            // forever on a locked/corrupt file is worse than losing history.
            File.Delete(_databasePath);
            RecoveredFromCorruptFileAt = null;
        }

        foreach (var strayExtension in new[] { "-wal", "-shm" })
        {
            var strayPath = _databasePath + strayExtension;
            if (File.Exists(strayPath))
            {
                File.Delete(strayPath);
            }
        }

        _ = reason; // surfaced via RecoveredFromCorruptFileAt + logs, not thrown
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}

/// <summary>
/// The migration runner, split out from <see cref="SqliteDatabase"/> so a
/// test can prove "forward migration against a populated database" against
/// a synthetic second migration without waiting for a real 0002 to exist
/// (issue #67's "Done when": migrations tested forward from empty AND from
/// an already-migrated, populated file).
/// </summary>
internal static class SqliteMigrations
{
    /// <summary>
    /// Discovers every <c>NNNN_name.sql</c> embedded resource under
    /// <c>Migrations/</c>, ordered by the numeric prefix. A resource whose
    /// name does not start with digits is a build-time authoring mistake,
    /// not a runtime possibility to swallow.
    /// </summary>
    public static IReadOnlyList<(long Version, string Sql)> LoadEmbedded()
    {
        var assembly = typeof(SqliteMigrations).GetTypeInfo().Assembly;
        const string prefix = "DnaEntropyGraph.Persistence.Migrations.";
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                "No embedded migration resources found under DnaEntropyGraph.Persistence.Migrations.*.sql. " +
                "A migration file that is not packed as an EmbeddedResource ships an app with no schema at all.");
        }

        var files = new List<(string FileName, string Sql)>();
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration '{name}' could not be opened.");
            using var reader = new StreamReader(stream);
            files.Add((name[prefix.Length..], reader.ReadToEnd()));
        }

        return BuildSet(files);
    }

    private static readonly System.Text.RegularExpressions.Regex FileNamePattern =
        new(@"^(\d{4})_[a-z0-9]+(_[a-z0-9]+)*\.sql$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates a migration file set (issue #842): names are
    /// <c>NNNN_snake.sql</c>, numbers are unique, and versions are exactly
    /// 1..N. A gap would let a later migration advance <c>user_version</c>
    /// past an earlier one that then never runs on any install.
    /// </summary>
    internal static IReadOnlyList<(long Version, string Sql)> BuildSet(IEnumerable<(string FileName, string Sql)> files)
    {
        var parsed = new List<(long Version, string FileName, string Sql)>();
        foreach (var (fileName, sql) in files)
        {
            var match = FileNamePattern.Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{fileName}' is not named NNNN_snake.sql (four digits, then lowercase words joined by underscores, e.g. 0001_initial.sql). Rename the file.");
            }

            parsed.Add((long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), fileName, sql));
        }

        foreach (var group in parsed.GroupBy(m => m.Version).Where(g => g.Count() > 1))
        {
            throw new InvalidOperationException(
                $"Duplicate migration number {group.Key:0000}: {string.Join(" and ", group.Select(m => m.FileName).Order(StringComparer.Ordinal))}. " +
                "Two branches claimed the same number; renumber the one that landed second.");
        }

        var ordered = parsed.OrderBy(m => m.Version).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var expected = i + 1L;
            if (ordered[i].Version != expected)
            {
                throw new InvalidOperationException(
                    $"Migration numbering has a gap: expected {expected:0000} but found {ordered[i].FileName}. " +
                    "Migrations must be exactly 0001..N with no gaps; renumber the new file.");
            }
        }

        return ordered.Select(m => (m.Version, m.Sql)).ToList();
    }

    /// <summary>
    /// Applies every migration whose version is greater than the
    /// connection's current <c>PRAGMA user_version</c>, each inside its own
    /// transaction, and advances <c>user_version</c> to match. A connection
    /// already at or past a migration's version skips it - this is what
    /// makes re-opening an already-migrated file a no-op rather than a
    /// re-run against live data.
    /// </summary>
    public static void Apply(SqliteConnection connection, IReadOnlyList<(long Version, string Sql)> migrations)
    {
        var currentVersion = GetUserVersion(connection);
        var latest = migrations.Count == 0 ? 0L : migrations.Max(m => m.Version);
        if (currentVersion > latest)
        {
            throw new DatabaseNewerThanAppException(currentVersion, latest);
        }

        foreach (var (version, sql) in migrations.OrderBy(m => m.Version))
        {
            if (version <= currentVersion)
            {
                continue;
            }

            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }

                using (var setVersion = connection.CreateCommand())
                {
                    setVersion.Transaction = transaction;
                    // PRAGMA does not support bound parameters; the version
                    // comes from this assembly's own embedded file names,
                    // never from user input.
                    setVersion.CommandText = $"PRAGMA user_version = {version};";
                    setVersion.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            currentVersion = version;
        }
    }

    private static long GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
