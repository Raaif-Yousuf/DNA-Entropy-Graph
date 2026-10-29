namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Issues #100 and #557: what each run COST, as the spend ledger booked it. The ledger entry (kind <c>vm_runtime</c>, keyed by the run's job id) is the ONLY source of a
/// run's booked figure: the Results header and the Runs rows read it here, so they can never disagree with the month total. A run with no entry is simply absent.
/// </summary>
public interface IRunCostSource
{
    /// <summary>The booked estimate in USD, by job id, for every run that has a ledger entry. One read for any number of rows.</summary>
    Task<IReadOnlyDictionary<string, double>> GetBookedAsync(CancellationToken cancellationToken);
}
