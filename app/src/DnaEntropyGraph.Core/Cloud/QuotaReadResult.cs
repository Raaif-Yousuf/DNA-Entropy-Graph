namespace DnaEntropyGraph.Core.Cloud;

/// <summary>The Compute quota metric names this app reads (docs/cloud_design.md section 6) and the accelerator-type to metric map.</summary>
public static class QuotaMetrics
{
    public const string NvidiaL4Gpus = "NVIDIA_L4_GPUS";

    public const string NvidiaA100Gpus = "NVIDIA_A100_GPUS";

    public const string NvidiaA100Gpus80Gb = "NVIDIA_A100_80GB_GPUS";

    public const string NvidiaH100Gpus = "NVIDIA_H100_GPUS";

    /// <summary>The project-wide GPU cap (read from <c>projects.get</c>, not per region). A zero here overrides a positive regional number.</summary>
    public const string GpusAllRegions = "GPUS_ALL_REGIONS";

    /// <summary>
    /// The regional quota metric for a GPU accelerator type, or null when none is known (the caller must not guess a metric).
    /// <c>"gpu"</c> is the placeholder the preflight passes today (<c>PreflightChecks</c>) and means the first tier, the L4.
    /// A value that already is a metric name is returned as it is.
    /// </summary>
    public static string? ForAccelerator(string acceleratorType)
        => acceleratorType?.Trim().ToLowerInvariant() switch
        {
            "gpu" or "nvidia-l4" or "nvidia_l4_gpus" => NvidiaL4Gpus,
            "nvidia-tesla-a100" or "nvidia_a100_gpus" => NvidiaA100Gpus,
            "nvidia-a100-80gb" or "nvidia_a100_80gb_gpus" => NvidiaA100Gpus80Gb,
            "nvidia-h100-80gb" or "nvidia_h100_gpus" => NvidiaH100Gpus,
            _ => null,
        };
}

/// <summary>One quota line: the limit and what is already used (Compute reports both as numbers).</summary>
public sealed record QuotaMetric(string Metric, double Limit, double Usage)
{
    public double Remaining => Math.Max(0, Limit - Usage);
}

/// <summary>One of the two reads (<c>regions.list</c>, <c>projects.get</c>) that failed, classified, so a caller can name an action.</summary>
public sealed record QuotaReadFailure(string Call, CloudError Error, CloudErrorKind Kind);

/// <summary>The three answers a quota question can have. A read that failed is <see cref="Unknown"/>, never <see cref="None"/>.</summary>
public enum QuotaState
{
    Unknown,
    None,
    Available,
}

/// <summary>
/// A project's quota as read from Compute: every region's metrics (<c>regions.list</c>, null when that read failed) and the
/// project-wide GPUS_ALL_REGIONS line (<c>projects.get</c>, null when that read failed or the metric was absent). The two reads
/// fail independently and nothing here turns a failure into a zero (the prototype's <c>None</c> versus <c>{}</c>, issue #86).
/// </summary>
public sealed record QuotaReadResult(
    IReadOnlyDictionary<string, IReadOnlyList<QuotaMetric>>? Regions,
    QuotaMetric? AllRegionsGpus,
    IReadOnlyList<QuotaReadFailure> Failures)
{
    /// <summary>A read where nothing could be learned.</summary>
    public static QuotaReadResult Failed(params QuotaReadFailure[] failures) => new(null, null, failures);

    /// <summary>
    /// GPUs this project could still create in <paramref name="region"/>: the smaller of the regional remainder and the
    /// GPUS_ALL_REGIONS remainder. Null (unknown) when a needed read is missing, except that one known value below a whole GPU
    /// is a definite zero whatever the other read says (a regional 0 binds without the cap, and a cap of 0 binds without the region).
    /// A region present without the metric offers none of it; a region the read did not return is unknown.
    /// </summary>
    public double? GpuRemaining(string region, string regionalMetric)
    {
        double? regional = null;
        if (Regions is not null)
        {
            var match = Regions.FirstOrDefault(r => string.Equals(r.Key, region, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
            {
                regional = match.Value.FirstOrDefault(m => string.Equals(m.Metric, regionalMetric, StringComparison.Ordinal))?.Remaining ?? 0;
            }
        }

        var known = new[] { regional, AllRegionsGpus?.Remaining }.OfType<double>().ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var smallest = known.Min();
        return smallest < 1 || known.Count == 2 ? smallest : null;
    }

    public QuotaState GpuState(string region, string regionalMetric)
        => GpuRemaining(region, regionalMetric) switch
        {
            null => QuotaState.Unknown,
            < 1 => QuotaState.None,
            _ => QuotaState.Available,
        };
}
