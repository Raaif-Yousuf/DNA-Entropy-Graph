using Dapper;

namespace DnaEntropyGraph.Persistence;

/// <summary>SQLite-backed <see cref="ICostLedgerRepository"/>. See that file for design notes.</summary>
public sealed class CostLedgerRepository : ICostLedgerRepository
{
    private const string SelectAllSql = """
        SELECT Id, RunId, ResourceId, Kind, StartedUtc, EndedUtc, UsdEst
        FROM CostLedger;
        """;

    private const string InsertSql = """
        INSERT INTO CostLedger (RunId, ResourceId, Kind, StartedUtc, EndedUtc, UsdEst)
        VALUES (@RunId, @ResourceId, @Kind, @StartedUtc, @EndedUtc, @UsdEst);
        SELECT last_insert_rowid();
        """;

    private readonly SqliteDatabase _database;

    public CostLedgerRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<CostLedgerEntry>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(SelectAllSql, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<Row>(command).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<CostLedgerEntry> AppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
    {
        using var connection = _database.OpenConnection();
        var command = new CommandDefinition(InsertSql, ToRow(entry), cancellationToken: cancellationToken);
        var newId = await connection.ExecuteScalarAsync<long>(command).ConfigureAwait(false);
        return entry with { Id = newId };
    }

    private static CostLedgerEntry ToRecord(Row row) => new(
        Kind: row.Kind,
        StartedUtc: Iso.Parse(row.StartedUtc)!.Value,
        RunId: row.RunId,
        ResourceId: row.ResourceId,
        EndedUtc: Iso.Parse(row.EndedUtc),
        UsdEst: row.UsdEst,
        Id: row.Id);

    private static Row ToRow(CostLedgerEntry entry) => new()
    {
        RunId = entry.RunId,
        ResourceId = entry.ResourceId,
        Kind = entry.Kind,
        StartedUtc = Iso.Format(entry.StartedUtc)!,
        EndedUtc = Iso.Format(entry.EndedUtc),
        UsdEst = entry.UsdEst,
    };

    private sealed class Row
    {
        public long Id { get; set; }
        public string? RunId { get; set; }
        public long? ResourceId { get; set; }
        public string Kind { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string? EndedUtc { get; set; }
        public double? UsdEst { get; set; }
    }
}
