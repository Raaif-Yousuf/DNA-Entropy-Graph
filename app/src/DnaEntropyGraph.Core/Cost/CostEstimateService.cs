using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>What the New run page asks before Run (issue #98).</summary>
public interface ICostEstimateService
{
    /// <summary>The estimate for a run on <paramref name="machineType"/> with a boot disk of <paramref name="bootDiskGb"/> GB. Never throws except for cancellation: a problem is a <see cref="CostEstimateResult"/> with a reason.</summary>
    Task<CostEstimateResult> EstimateAsync(string machineType, bool spot, long? bases, int bootDiskGb, CancellationToken cancellationToken);

    /// <summary>
    /// What the monthly spending cap says about a run estimated at <paramref name="estimatedUsd"/> (issue #109). The default says
    /// "no cap", so a service with no ledger behind it shows no card.
    /// </summary>
    Task<SpendCapStatus?> CheckSpendCapAsync(double? estimatedUsd, CancellationToken cancellationToken)
        => Task.FromResult<SpendCapStatus?>(null);

    /// <summary>
    /// Raised (on whatever thread the price read finished on) when a background price read changed the prices an estimate uses, or (for a source that
    /// reports one) where they came from, why and as of when, so what is on screen was made from older facts and the page should ask again.
    /// Never raised for a read that changed none of those.
    /// </summary>
    event Action? PricesChanged;
}

/// <summary>Joins the shipped price list, this PC's run history and <see cref="CostEstimator"/>.</summary>
public sealed class CostEstimateService : ICostEstimateService, IDisposable
{
    /// <summary>
    /// Cancelled when the service is disposed, so a background price read is owned by something. MEASURED 2026-10-29 (grep of app/src): nothing disposes the DI container
    /// in the shipped app today (App.xaml.cs builds the provider and never disposes it), so in the app this is NOT what ends the read: at exit the process ends it.
    /// That is safe because the cache write is tmp-then-move (<see cref="FilePricingCacheStore"/>), so a read cut mid-way leaves the old cache whole. Dispose is kept for tests and for an owner that disposes later.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    public event Action? PricesChanged;

    public void Dispose() => _lifetime.Cancel();

    private readonly Lazy<PricingLoadResult> _pricing;
    private readonly IPricingSource _source;
    private readonly IRunRepository _runs;
    private readonly TimeSpan _refreshWait;
    private readonly object _refreshLock = new();
    private Task? _refreshing;

    /// <summary>How long an estimate waits for a price refresh that is still running before it answers from what is known now.</summary>
    public static readonly TimeSpan DefaultRefreshWait = TimeSpan.FromSeconds(3);

    private readonly ISpendCapGate? _gate;

    /// <summary>The estimate service with no spend cap behind it and a chosen refresh wait.</summary>
    public CostEstimateService(IPricingSource pricing, IRunRepository runs, TimeSpan refreshWait)
        : this(pricing, runs, null, refreshWait)
    {
    }

    public CostEstimateService(IPricingSource pricing, IRunRepository runs, ISpendCapGate? gate = null, TimeSpan? refreshWait = null)
    {
        _refreshWait = refreshWait ?? DefaultRefreshWait;
        _pricing = new Lazy<PricingLoadResult>(pricing.Load);
        _source = pricing;
        _runs = runs;
        _gate = gate;
    }

    public async Task<SpendCapStatus?> CheckSpendCapAsync(double? estimatedUsd, CancellationToken cancellationToken)
    {
        if (_gate is null)
        {
            return null;
        }

        try
        {
            return await _gate.CheckAsync(estimatedUsd, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The card is advice; a ledger that cannot be read must not take the page down. The engine's own check still runs at Run.
            return null;
        }
    }

    public async Task<CostEstimateResult> EstimateAsync(string machineType, bool spot, long? bases, int bootDiskGb, CancellationToken cancellationToken)
    {
        // Issue #214: a source that can ask the price catalog (at most once a day) does so and is read again each time, because its answer
        // can change under a running app. The shipped list cannot, so it is read once. The page is waiting, so a refresh that is still
        // running after a few seconds is left to finish in the background (review M1): this estimate uses what is known now, the next the new prices.
        if (_source is IPricingRefresh refresh)
        {
            await WaitForRefreshAsync(refresh, cancellationToken).ConfigureAwait(false);
        }

        var pricing = _source is IPricingRefresh ? _source.Load() : _pricing.Value;
        if (pricing.Table is not { } table)
        {
            return CostEstimateResult.Unavailable(EstimateUnavailable.PriceListUnreadable);
        }

        IReadOnlyList<TimeSpan> history;
        try
        {
            history = RunHistory.DurationsFor(await _runs.GetAllAsync(cancellationToken), machineType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // History only sharpens the number. A database that cannot be read must not take the estimate away.
            history = [];
        }

        return CostEstimator.Estimate(table, RunTimeModel.Default, new CostEstimateRequest(machineType, spot, bases, bootDiskGb, history));
    }

    private async Task WaitForRefreshAsync(IPricingRefresh refresh, CancellationToken cancellationToken)
    {
        Task running;
        lock (_refreshLock)
        {
            // One read at a time: a call that finds the last one still running waits on that one, never starts another.
            if (_refreshing is null || _refreshing.IsCompleted)
            {
                _refreshing = RunRefreshAsync(refresh);
            }

            running = _refreshing;
        }

        if (!running.IsCompleted)
        {
            await Task.WhenAny(running, Task.Delay(_refreshWait, cancellationToken)).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // Not tied to the estimate's token: the page cancels an estimate whenever the inputs change, and that must not abandon the daily read.
    // It is tied to the service's lifetime instead (Dispose). Started on the thread pool: the source reads files before it asks the network,
    // and the caller of an estimate can be the UI thread.
    // A source never throws but for cancellation (IPricingRefresh); anything else is swallowed here so a background task cannot fault unobserved.
    private Task RunRefreshAsync(IPricingRefresh refresh)
    {
        var lifetime = _lifetime.Token;
        return Task.Run(async () =>
        {
            try
            {
                var before = Fingerprint();
                await refresh.RefreshIfStaleAsync(lifetime).ConfigureAwait(false);
                if (before != Fingerprint())
                {
                    PricesChanged?.Invoke();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The source is left on whatever it was serving; a handler that threw is the page's problem, not the read's.
            }
        });
    }

    /// <summary>
    /// What a page made from this source shows right now: every machine row and the disk price, then (for a source that reports one) where those
    /// prices came from, why and as of when. Null when it serves no prices. The status is part of it because the price source line under the
    /// estimate is made from it: a read that fails, or succeeds with the very same prices on a new day, changes the line without changing a price.
    /// Deliberately not the table's JSON, which carries a source note that is not shown.
    /// </summary>
    private string? Fingerprint()
    {
        if (_source.Load().Table is not { } table)
        {
            return null;
        }

        var rows = table.Machines
            .OrderBy(m => m.MachineType, StringComparer.Ordinal)
            .Select(m => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{m.MachineType}|{m.Accelerator}|{m.OnDemandUsdPerHour:R}|{m.SpotUsdPerHour:R}"));
        var prices = string.Join(';', rows) + string.Create(System.Globalization.CultureInfo.InvariantCulture, $";disk={table.DiskUsdPerGbMonth:R}");
        if (_source is not IPricingStatus status)
        {
            return prices;
        }

        var current = status.Current;
        var spotKept = current.SpotKeptFromShipped is { Count: > 0 } kept ? string.Join(',', kept) : string.Empty;
        return prices + string.Create(System.Globalization.CultureInfo.InvariantCulture, $";status={current.Origin}|{current.Reason}|{current.AsOf:O}|{spotKept}");
    }
}
