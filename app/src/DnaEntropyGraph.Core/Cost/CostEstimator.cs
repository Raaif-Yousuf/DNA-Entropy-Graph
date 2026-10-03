namespace DnaEntropyGraph.Core.Cost;

/// <summary>
/// How long a run takes when there is no history to say. THEORY (unverified): the fixed minutes are the defaults in
/// docs/cloud_design.md section 10 (12 minutes on a fresh computer that must start, pull the container and fetch the model;
/// 5 minutes on a stopped one the app reuses), and the predict rate is spec section 5.7's "30 kb in about a minute" on an L4 with
/// both directions on. They stay THEORY until the first GPU acceptance run records real numbers.
/// </summary>
public sealed record RunTimeModel(double FreshFixedMinutes, double WarmFixedMinutes, double PredictSecondsPerKb)
{
    public static RunTimeModel Default { get; } = new(FreshFixedMinutes: 12, WarmFixedMinutes: 5, PredictSecondsPerKb: 2.0);

    public double PredictMinutes(long bases) => bases / 1000.0 * PredictSecondsPerKb / 60;
}

/// <summary>What the estimate is made from, so the page and the docs can say so.</summary>
public enum EstimateBasis
{
    /// <summary>The median duration of this machine's past completed runs on this PC.</summary>
    History,

    /// <summary>The documented <see cref="RunTimeModel"/>, because there is no history yet.</summary>
    Model,
}

/// <summary>Why there is no estimate. Unknown stays unknown: nothing here is guessed.</summary>
public enum EstimateUnavailable
{
    /// <summary>No history and no sequence length yet (the file is still being checked, or is not valid).</summary>
    UnknownSize,

    /// <summary>The price list that ships with the app could not be read.</summary>
    PriceListUnreadable,

    NoPriceForMachine,

    /// <summary>Spot was asked for and the price list has no spot price for this machine.</summary>
    NoSpotPrice,
}

/// <param name="MachineType">The Compute Engine machine type of the tier (for example <c>g2-standard-8</c>).</param>
/// <param name="Spot">Whether the run uses a Spot VM.</param>
/// <param name="Bases">Total bases in the input, or null when not known yet.</param>
/// <param name="PastDurations">Durations of past completed runs on this machine, see <see cref="RunHistory"/>.</param>
public sealed record CostEstimateRequest(string MachineType, bool Spot, long? Bases, IReadOnlyList<TimeSpan> PastDurations);

/// <summary>A cost and time range in US dollars and minutes; a point when min equals max. Always an estimate, never an invoice figure.</summary>
public sealed record CostEstimate(double MinMinutes, double MaxMinutes, double MinUsd, double MaxUsd, EstimateBasis Basis)
{
    public bool IsPoint => MinMinutes == MaxMinutes;
}

public sealed record CostEstimateResult(CostEstimate? Estimate, EstimateUnavailable? Reason)
{
    public static CostEstimateResult Of(CostEstimate estimate) => new(estimate, null);

    public static CostEstimateResult Unavailable(EstimateUnavailable reason) => new(null, reason);
}

/// <summary>
/// The arithmetic only (issue #98): minutes times the hourly rate of the machine plus the hourly share of the boot disk.
/// No file, clock or network is touched here.
/// </summary>
public static class CostEstimator
{
    public static CostEstimateResult Estimate(PricingTable pricing, RunTimeModel model, CostEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);

        var machine = pricing.Find(request.MachineType);
        if (machine is null)
        {
            return CostEstimateResult.Unavailable(EstimateUnavailable.NoPriceForMachine);
        }

        var machineRate = request.Spot ? machine.SpotUsdPerHour : machine.OnDemandUsdPerHour;
        if (machineRate is null)
        {
            return CostEstimateResult.Unavailable(EstimateUnavailable.NoSpotPrice);
        }

        var hourly = machineRate.Value + pricing.DiskUsdPerHour;
        if (request.PastDurations.Count > 0)
        {
            var minutes = MedianMinutes(request.PastDurations);
            return CostEstimateResult.Of(new CostEstimate(minutes, minutes, Cost(hourly, minutes), Cost(hourly, minutes), EstimateBasis.History));
        }

        if (request.Bases is not { } bases)
        {
            return CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize);
        }

        var predict = model.PredictMinutes(bases);
        var low = model.WarmFixedMinutes + predict;
        var high = model.FreshFixedMinutes + predict;
        return CostEstimateResult.Of(new CostEstimate(low, high, Cost(hourly, low), Cost(hourly, high), EstimateBasis.Model));
    }

    private static double Cost(double hourlyUsd, double minutes) => hourlyUsd * minutes / 60;

    private static double MedianMinutes(IReadOnlyList<TimeSpan> durations)
    {
        var sorted = durations.Select(d => d.TotalMinutes).Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
