namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>What one run's ledger entry booked: the estimate in USD and the seconds of VM time it was booked for (its frozen <c>EndedUtc - StartedUtc</c>, never less than zero).</summary>
public readonly record struct BookedRun(double Usd, long Seconds);

/// <summary>
/// Issues #100 and #557: what each run COST, as the spend ledger booked it. The ledger entry (kind <c>vm_runtime</c>, keyed by the run's job id) is the ONLY source of a
/// run's booked figure: the Results header and the Runs rows read it here, so they can never disagree with the month total. A run with no entry is simply absent.
/// </summary>
public interface IRunCostSource
{
    /// <summary>The booked estimate in USD, by job id, for every run that has a ledger entry. One read for any number of rows.</summary>
    Task<IReadOnlyDictionary<string, double>> GetBookedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The cost AND the seconds of VM time each run's ledger entry booked, by job id, from ONE read of the ledger: the support zip's figures can never describe two different entries or
    /// be half present (a booking between two reads, or the second read failing). A run with no entry is absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, BookedRun>> GetBookedRunsAsync(CancellationToken cancellationToken);
}
