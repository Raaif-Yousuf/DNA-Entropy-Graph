using System.Globalization;
using System.Numerics;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The "Cloud results retention" setting (issue #114): how many days the results bucket keeps a job's objects before its own
/// lifecycle rule deletes them. Stored in <c>settings.json</c> under <see cref="Key"/> and read at each use (the storage gateway asks
/// once at the start of every <c>EnsureBucketAsync</c>), so a change reaches the bucket on the next run without restarting the app.
/// The Settings "Cloud results" group (<c>CloudResultsSettingsViewModel</c>) writes it.
/// The gateway never shortens a longer rule another installation set (#597); a confirmed shortening is #598.
/// </summary>
public static class ResultsRetentionSetting
{
    /// <summary>The settings key.</summary>
    public const string Key = "results_retention_days";

    /// <summary>
    /// The configured days. An absent, zero, negative, unreadable or unavailable value is <see cref="ResultsBucket.DefaultRetentionDays"/>
    /// (never "keep nothing": 0 would delete results at once); a value above <see cref="ResultsBucket.MaxRetentionDays"/> is clamped to it.
    /// A whole number written with a decimal point ("30.0") is that number; a fraction or an exponent ("30.5", "1e2") is unusable and
    /// logs a warning (the class of the problem, never the value). Never throws, so a bad file cannot stop a run before the bucket step.
    /// </summary>
    public static int Days(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? raw;
        try
        {
            raw = settings.GetString(Key)?.Trim();
        }
        catch (SettingsUnavailableException)
        {
            return ResultsBucket.DefaultRetentionDays;
        }

        if (string.IsNullOrEmpty(raw))
        {
            return ResultsBucket.DefaultRetentionDays;
        }

        if (!BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            if (decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d))
            {
                parsed = new BigInteger(d);
            }
            else
            {
                System.Diagnostics.Trace.TraceWarning("results_retention_days is not a whole number of days; using the default");
                return ResultsBucket.DefaultRetentionDays;
            }
        }

        return parsed >= 1
            ? (int)BigInteger.Min(parsed, ResultsBucket.MaxRetentionDays)
            : ResultsBucket.DefaultRetentionDays;
    }
}
