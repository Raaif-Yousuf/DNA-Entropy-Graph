namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Issues #100 and #557: the one writer of a missed cost booking. A ledger append that failed after its run row was stored is re-driven here, once per launch
/// (<c>AppStartup</c>); a page, the Results header or the support zip reading a run's cost never does. An interface so a test can count the calls.
/// </summary>
public interface IRunCostRepair
{
    /// <summary>Books every ended run that has a VM and no ledger entry yet, and nothing else. Idempotent.</summary>
    Task BookUnbookedRunsAsync(CancellationToken cancellationToken);
}
