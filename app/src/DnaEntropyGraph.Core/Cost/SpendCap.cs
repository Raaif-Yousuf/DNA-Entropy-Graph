using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>What the monthly spending cap says about a run (issue #109).</summary>
public enum SpendCapLevel
{
    /// <summary>No cap is set, or this month's spend plus the run stays under it: nothing to show.</summary>
    None,

    /// <summary>The cap would be reached: say so and let the run start.</summary>
    Warning,

    /// <summary>The cap would be reached and the hard stop is on: the run does not start.</summary>
    Blocked,
}

/// <param name="Level">What to do.</param>
/// <param name="MonthToDateUsd">This calendar month's estimated spend from the app's own ledger (local time).</param>
/// <param name="CapUsd">The cap, or null when none is set.</param>
public sealed record SpendCapStatus(SpendCapLevel Level, double MonthToDateUsd, double? CapUsd);

/// <summary>Reads the monthly spend and checks it against the cap. Implemented over the cost ledger.</summary>
public interface ISpendCapGate
{
    /// <summary>
    /// Whether a run estimated at <paramref name="estimatedUsd"/> (null when unknown, counted as zero) would reach the cap:
    /// month-to-date plus the estimate at or above the cap.
    /// </summary>
    Task<SpendCapStatus> CheckAsync(double? estimatedUsd, CancellationToken cancellationToken);

    /// <summary>The estimated spend recorded so far in the current local calendar month.</summary>
    Task<double> MonthToDateUsdAsync(CancellationToken cancellationToken);
}

/// <summary>The two settings the cap reads from <c>settings.json</c>. The Settings page writes them; the gate reads them at every check.</summary>
public static class SpendCapSettings
{
    public const string CapUsdKey = "spend_cap_usd";

    public const string HardStopKey = "spend_cap_hard_stop";

    /// <summary>The cap in US dollars. Absent, unreadable, zero, negative or not finite is no cap.</summary>
    public static double? ReadCapUsd(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return TryParseCap(settings.GetString(CapUsdKey), out var cap) ? cap : null;
    }

    public static bool IsHardStop(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Equals(settings.GetString(HardStopKey)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a cap the way both the gate and the Settings box do, so what the box accepts is what the gate reads.</summary>
    public static bool TryParseCap(string? text, out double cap)
    {
        cap = 0;
        return double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out cap) && double.IsFinite(cap) && cap > 0;
    }
}
