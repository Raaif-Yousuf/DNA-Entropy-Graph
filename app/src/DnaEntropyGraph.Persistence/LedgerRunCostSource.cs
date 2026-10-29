using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// Reads each run's booked cost from the cost ledger (see <see cref="IRunCostSource"/>): one read of the ledger's <c>vm_runtime</c> entries (filtered in the query), the first entry
/// of a run standing. With a <paramref name="repair"/> it first books any ended run whose ledger append failed (the same idempotent reconcile a month-to-date read makes).
/// </summary>
public sealed class LedgerRunCostSource(ICostLedgerRepository ledger, SpendLedger? repair = null) : IRunCostSource
{
    public async Task<IReadOnlyDictionary<string, double>> GetBookedAsync(CancellationToken cancellationToken)
    {
        if (repair is not null)
        {
            try
            {
                await repair.BookUnbookedRunsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A repair that cannot run leaves the figures the ledger already holds; the page is never failed for it.
            }
        }

        var booked = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in await ledger.GetByKindAsync(SpendLedger.VmRuntimeKind, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Kind == SpendLedger.VmRuntimeKind && entry.RunId is { } run && entry.UsdEst is { } usd && double.IsFinite(usd) && usd >= 0)
            {
                booked.TryAdd(run, usd);
            }
        }

        return booked;
    }
}
