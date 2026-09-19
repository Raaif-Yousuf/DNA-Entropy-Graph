using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Owns the active runners and publishes phase/progress changes
/// (docs/architecture.md section 3 - implemented by the App-layer
/// singleton <c>JobEngine</c>, which is why Presentation depends only on
/// this interface and never on Cloud or LocalEngine directly).
/// </summary>
public interface IJobEngine
{
    Task<string> StartRunAsync(RunOptions options, CancellationToken cancellationToken);

    Task CancelRunAsync(string jobId, CancellationToken cancellationToken);

    IReadOnlyList<WorkerResult> CompletedRuns { get; }
}
