using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// Reads each run's booked cost from the cost ledger (see <see cref="IRunCostSource"/>): one read of the ledger's <c>vm_runtime</c> entries (filtered in the query), the first entry
/// of a run standing. A read never writes: a ledger append that failed after its run row was stored is booked by the repair the app runs once at startup
/// (<see cref="SpendLedger.BookUnbookedRunsAsync"/>) and by a month-to-date read, never by a page, the Results header or the support zip reading a cost.
/// </summary>
public sealed class LedgerRunCostSource(ICostLedgerRepository ledger) : IRunCostSource
{
    public async Task<IReadOnlyDictionary<string, double>> GetBookedAsync(CancellationToken cancellationToken)
    {
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

    public async Task<IReadOnlyDictionary<string, long>> GetBookedSecondsAsync(CancellationToken cancellationToken)
    {
        var seconds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in await ledger.GetByKindAsync(SpendLedger.VmRuntimeKind, cancellationToken).ConfigureAwait(false))
        {
            // The same entry GetBookedAsync reads (the first of a run stands), so the cost and the seconds are one booking.
            if (entry.Kind == SpendLedger.VmRuntimeKind && entry.RunId is { } run && entry.UsdEst is { } usd && double.IsFinite(usd) && usd >= 0)
            {
                seconds.TryAdd(run, Math.Max(0L, (long)Math.Round(((entry.EndedUtc ?? entry.StartedUtc) - entry.StartedUtc).TotalSeconds)));
            }
        }

        return seconds;
    }
}
