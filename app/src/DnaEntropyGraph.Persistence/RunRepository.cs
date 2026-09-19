using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// In-memory placeholder for the SQLite-backed <c>Runs</c> table
/// (docs/architecture.md section 6). See this file's csproj comment.
/// </summary>
public sealed class RunRepository : IRunRepository
{
    private readonly ConcurrentDictionary<string, RunRecord> _runs = new();

    public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RunRecord>>(_runs.Values.ToList());

    public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
    {
        _runs[run.JobId] = run;
        return Task.CompletedTask;
    }
}
