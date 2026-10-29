using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>Reads each run's booked cost from the cost ledger (see <see cref="IRunCostSource"/>): one read of the ledger's <c>vm_runtime</c> entries, the first entry of a run standing.</summary>
public sealed class LedgerRunCostSource(ICostLedgerRepository ledger) : IRunCostSource
{
    public async Task<IReadOnlyDictionary<string, double>> GetBookedAsync(CancellationToken cancellationToken)
    {
        var booked = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in await ledger.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (entry.Kind == SpendLedger.VmRuntimeKind && entry.RunId is { } run && entry.UsdEst is { } usd && double.IsFinite(usd) && usd >= 0)
            {
                booked.TryAdd(run, usd);
            }
        }

        return booked;
    }
}
