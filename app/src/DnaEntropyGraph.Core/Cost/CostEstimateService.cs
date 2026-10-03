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
    private readonly IRunRepository _runs;

    public CostEstimateService(IPricingSource pricing, IRunRepository runs)
    {
        _pricing = new Lazy<PricingLoadResult>(pricing.Load);
        _runs = runs;
    }

    public async Task<CostEstimateResult> EstimateAsync(string machineType, bool spot, long? bases, int bootDiskGb, CancellationToken cancellationToken)
    {
        // The list ships with the build and cannot change under a running app, so it is read once.
        if (_pricing.Value.Table is not { } table)
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
}
