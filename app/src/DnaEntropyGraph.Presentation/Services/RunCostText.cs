using System.Globalization;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The words for a run's cost (issues #100 and #557). The ONE figure is the run's spend-ledger entry (<see cref="IRunCostSource"/>), booked when the run ended
/// (VM time at the list price, Spot rate for a Spot run, plus the disk share); this class never computes money, it only formats the figure it is given, so the
/// Results header, the Runs row and the ledger can never disagree. Neither <see cref="RunRecord.EstimatedCostUsd"/> nor <see cref="RunRecord.ActualCostUsd"/> is read:
/// nothing writes them (no billing export access, see cloud_design.md). Null means "say nothing": a cloud run with no ledger entry (no VM ever existed, or still going)
/// shows no figure, never $0.00.
/// </summary>
public static class RunCostText
{

    /// <summary>The whole-dollars-and-cents text of a figure: rounded to the cent, never below one cent (a real VM ran).</summary>
    public static string Money(double usd)
        => Math.Max(0.01, Math.Round(usd, 2, MidpointRounding.AwayFromZero)).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the price list has a Spot rate for a machine, the same question the ledger asks when it prices a Spot run; null (no price source) trusts the run's own flag.
    /// A list that cannot be read or does not know the machine answers yes: no figure is shown in that case either way, and the flag is all there is.
    /// </summary>
    public static Func<string?, bool>? SpotPriceLookup(IPricingSource? pricing)
    {
        if (pricing is null)
        {
            return null;
        }

        var table = new Lazy<PricingTable?>(() => pricing.Load().Table);
        return machine => machine is null || table.Value?.Find(machine) is not { } found || found.SpotUsdPerHour is not null;
    }

    /// <summary>
    /// The booked figures, by job id: one read of the ledger. A source that is absent or fails shows no figure (a cost line is never worth failing a page for); only the
    /// caller giving up propagates.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, double>> ReadBookedAsync(IRunCostSource? source, CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return new Dictionary<string, double>();
        }

        try
        {
            return await source.GetBookedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Dictionary<string, double>();
        }
    }

    /// <summary>The sentence under the Results title, or null when there is nothing to say.</summary>
    public static string? Header(RunRecord run, double? bookedUsd, IStringResourceProvider strings, Func<string?, bool>? hasSpotPrice = null)
        => Build(run, bookedUsd, strings, ResultsCopy.CostNoCloud, ResultsCopy.CostEstimate, ResultsCopy.CostEstimateSpot, hasSpotPrice);

    /// <summary>The short line on a Runs row, or null when there is nothing to say.</summary>
    public static string? Short(RunRecord run, double? bookedUsd, IStringResourceProvider strings, Func<string?, bool>? hasSpotPrice = null)
        => Build(run, bookedUsd, strings, ResultsCopy.CostShortNoCloud, ResultsCopy.CostShort, ResultsCopy.CostShortSpot, hasSpotPrice);

    private static string? Build(RunRecord run, double? bookedUsd, IStringResourceProvider strings, string noCloudKey, string estimateKey, string spotKey, Func<string?, bool>? hasSpotPrice)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.Equals(run.Target, RunTargets.LocalRecord, StringComparison.OrdinalIgnoreCase))
        {
            return strings.GetString(noCloudKey);
        }

        // KNOWN LIMIT: the ledger entry does not record which rate it used, so "Spot" is the run's own flag plus the current price list: the ledger charges the Spot rate only\n        // when the machine HAS one (SpendLedger.UsdFor falls back to on-demand), so the label asks the same question. The figure itself is always the ledger's.
        var spotRate = run.IsSpot == true && (hasSpotPrice is null || hasSpotPrice(run.MachineType));
        return bookedUsd is { } usd && double.IsFinite(usd) && usd >= 0
            ? string.Format(CultureInfo.CurrentCulture, strings.GetString(spotRate ? spotKey : estimateKey), Money(usd))
            : null;
    }
}
