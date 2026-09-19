namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// The <c>Runs</c> table (docs/architecture.md section 6). Implemented on
/// SQLite in DnaEntropyGraph.Persistence; this skeleton's implementation is
/// in-memory (see RunRepository there) until the real migrations land.
/// </summary>
public interface IRunRepository
{
    Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken);

    Task UpsertAsync(RunRecord run, CancellationToken cancellationToken);
}

public sealed record RunRecord(string JobId, JobPhase Phase, DateTimeOffset CreatedUtc);
