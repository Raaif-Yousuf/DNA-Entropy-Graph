using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The words for a run's cost (issues #100 and #557). The ONE figure is <see cref="RunRecord.EstimatedCostUsd"/>, booked by the spend
/// ledger when the run ended (VM time at the list price, Spot rate for a Spot run, plus the disk share); this class never computes
/// money, it only formats that column, so the Results header, the Runs row and the ledger can never disagree.
/// <see cref="RunRecord.ActualCostUsd"/> is deliberately not read: nothing writes it (no billing export access, see cloud_design.md).
/// Null means "say nothing": a cloud run that booked nothing (no VM ever existed, or still going) shows no figure, never $0.00.
/// </summary>
public static class RunCostText
{
    private const string LocalTarget = "local";

    /// <summary>The whole-dollars-and-cents text of a figure: rounded to the cent, never below one cent (a real VM ran).</summary>
    public static string Money(double usd)
        => Math.Max(0.01, Math.Round(usd, 2, MidpointRounding.AwayFromZero)).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The sentence under the Results title, or null when there is nothing to say.</summary>
    public static string? Header(RunRecord run, IStringResourceProvider strings)
        => Build(run, strings, ResultsCopy.CostNoCloud, ResultsCopy.CostEstimate, ResultsCopy.CostEstimateSpot);

    /// <summary>The short line on a Runs row, or null when there is nothing to say.</summary>
    public static string? Short(RunRecord run, IStringResourceProvider strings)
        => Build(run, strings, ResultsCopy.CostShortNoCloud, ResultsCopy.CostShort, ResultsCopy.CostShortSpot);

    private static string? Build(RunRecord run, IStringResourceProvider strings, string noCloudKey, string estimateKey, string spotKey)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.Equals(run.Target, LocalTarget, StringComparison.Ordinal))
        {
            return strings.GetString(noCloudKey);
        }

        return run.EstimatedCostUsd is { } usd
            ? string.Format(CultureInfo.CurrentCulture, strings.GetString(run.IsSpot == true ? spotKey : estimateKey), Money(usd))
            : null;
    }
}
