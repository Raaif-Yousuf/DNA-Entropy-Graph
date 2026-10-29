using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>What the New run page asks before Run (issue #98).</summary>
public interface ICostEstimateService
{
    /// <summary>The estimate for a run on <paramref name="machineType"/> with a boot disk of <paramref name="bootDiskGb"/> GB. Never throws except for cancellation: a problem is a <see cref="CostEstimateResult"/> with a reason.</summary>
    Task<CostEstimateResult> EstimateAsync(string machineType, bool spot, long? bases, int bootDiskGb, CancellationToken cancellationToken);
}

/// <summary>Joins the shipped price list, this PC's run history and <see cref="CostEstimator"/>.</summary>
public sealed class CostEstimateService : ICostEstimateService
{
    private readonly Lazy<PricingLoadResult> _pricing;
    private readonly IPricingSource _source;
    private readonly IRunRepository _runs;
    private readonly TimeSpan _refreshWait;
    private readonly object _refreshLock = new();
    private Task? _refreshing;

    /// <summary>How long an estimate waits for a price refresh that is still running before it answers from what is known now.</summary>
    public static readonly TimeSpan DefaultRefreshWait = TimeSpan.FromSeconds(3);

    public CostEstimateService(IPricingSource pricing, IRunRepository runs, TimeSpan? refreshWait = null)
    {
        _refreshWait = refreshWait ?? DefaultRefreshWait;
        _pricing = new Lazy<PricingLoadResult>(pricing.Load);
        _source = pricing;
        _runs = runs;
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
    // A source never throws but for cancellation (IPricingRefresh); anything else is swallowed here so a background task cannot fault unobserved.
    private static async Task RunRefreshAsync(IPricingRefresh refresh)
    {
        try
        {
            await refresh.RefreshIfStaleAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The source is left on whatever it was serving.
        }
    }
}
