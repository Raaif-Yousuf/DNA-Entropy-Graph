namespace DnaEntropyGraph.Persistence;

/// <summary>
/// The <c>CostLedger</c> table (Appendix A section 3): an append-only log of
/// billable spans (vm_runtime | disk | storage), the source for the Cloud
/// page's spend history and the <c>MonthlySpend</c> cap banner (issue
/// #109's sibling work). See <c>ICloudResourceRepository</c> for why this
/// lives in <c>DnaEntropyGraph.Persistence</c> rather than
/// <c>Core.Abstractions</c> tonight.
/// </summary>
public interface ICostLedgerRepository
{
    Task<IReadOnlyList<CostLedgerEntry>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>The rows of one <see cref="CostLedgerEntry.Kind"/>, filtered in the query: a reader of one kind (the run cost source) never pulls the others.</summary>
    Task<IReadOnlyList<CostLedgerEntry>> GetByKindAsync(string kind, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new row and returns it with its DB-generated
    /// <see cref="CostLedgerEntry.Id"/> filled in. There is no Upsert here:
    /// a ledger is append-only by nature - a correction is a new entry, not
    /// an edit of history.
    /// </summary>
    Task<CostLedgerEntry> AppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="AppendAsync"/>, but a row for the same (RunId, Kind) that already exists makes this a no-op that returns null
    /// (migration 0003's unique index with INSERT OR IGNORE), so concurrent writers book a run once. Rows with no RunId always insert.
    /// </summary>
    Task<CostLedgerEntry?> TryAppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken);
}

/// <summary>One row of the <c>CostLedger</c> table. <see cref="Id"/> is 0 until <see cref="ICostLedgerRepository.AppendAsync"/> assigns it.</summary>
public sealed record CostLedgerEntry(
    string Kind,
    DateTimeOffset StartedUtc,
    string? RunId = null,
    long? ResourceId = null,
    DateTimeOffset? EndedUtc = null,
    double? UsdEst = null,
    long Id = 0);
