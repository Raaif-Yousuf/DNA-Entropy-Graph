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
    public static string? ForAccelerator(string acceleratorType, bool spot = false)
        => spot ? Preemptible(ForAccelerator(acceleratorType)) : acceleratorType?.Trim().ToLowerInvariant() switch
        {
            "gpu" or "nvidia-l4" or "nvidia_l4_gpus" => NvidiaL4Gpus,
            "nvidia-tesla-a100" or "nvidia_a100_gpus" => NvidiaA100Gpus,
            "nvidia-a100-80gb" or "nvidia_a100_80gb_gpus" => NvidiaA100Gpus80Gb,
            "nvidia-h100-80gb" or "nvidia_h100_gpus" => NvidiaH100Gpus,
            _ => null,
        };

    /// <summary>
    /// The Cloud Quotas API's <c>quotaId</c> for a metric this app may ask more of (issue #99), or null for any other (the Spot twin included: see
    /// docs/cloud_design.md section 6.x for why no preemptible request is made). MEASURED 2026-10-15 (docs.cloud.google.com/docs/quotas/api-overview, whose own
    /// <c>CreateQuotaPreference</c> example for a GPU quota is <c>"quotaId": "GPUS-PER-GPU-FAMILY-per-project-region"</c> with a <c>region</c> dimension, and
    /// docs.cloud.google.com/compute/resource-usage, which names the standard L4 quota <c>GPU_FAMILY:NVIDIA_L4</c>): the L4 is the per-GPU-family quota with
    /// <see cref="DimensionsFor"/> naming the family. THEORY (unverified, no live project): the <c>GPUS-ALL-REGIONS-per-project</c> id (the docs name the global quota
    /// only by its display name "GPUs (all regions)") and the <c>gpu_family</c> dimension key. An id Google does not know reads as an unknown eligibility (never
    /// "eligible") and a wrong dimension is a rejected request, so a wrong guess asks for nothing.
    /// </summary>
    public static string? QuotaIdFor(string metric) => metric switch
    {
        NvidiaL4Gpus => "GPUS-PER-GPU-FAMILY-per-project-region",
        GpusAllRegions => "GPUS-ALL-REGIONS-per-project",
        _ => null,
    };

    /// <summary>
    /// The <c>dimensions</c> of the quota preference for <paramref name="metric"/>: the region and the GPU family for the L4 (one quota id covers every family, the
    /// dimensions pick the L4 in one region), none for the project-wide cap. Two preferences with the same dimensions are the same preference (the API allows only one).
    /// </summary>
    public static IReadOnlyDictionary<string, string> DimensionsFor(string metric, string? region)
        => metric == NvidiaL4Gpus && !string.IsNullOrWhiteSpace(region)
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = region, ["gpu_family"] = "NVIDIA_L4" }
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The regional quota metric a Spot run of <paramref name="acceleratorType"/> consumes in a region whose quota lines are <paramref name="regionLines"/> (issue #718).
    /// MEASURED 2026-10-15 (docs.cloud.google.com/compute/resource-usage): without a preemptible quota grant Spot VMs "consume standard quota", and once one is granted they "can only
    /// consume preemptible quota". So the preemptible twin is read only when its line exists with a limit above zero; otherwise the standard metric is. THEORY (unverified, no
    /// live project): an ungranted region reports the preemptible line as 0 or not at all; both read as the standard metric here. Null when the accelerator has no known metric.
    /// </summary>
    public static string? SpotRegionalMetric(string acceleratorType, IEnumerable<QuotaMetric> regionLines)
    {
        var standard = ForAccelerator(acceleratorType);
        var twin = Preemptible(standard);
        return twin is not null && regionLines.Any(m => string.Equals(m.Metric, twin, StringComparison.Ordinal) && m.Limit is > 0) ? twin : standard;
    }

    /// <summary>The Spot (preemptible) twin's NAME for a regional GPU metric. It is only the line a Spot run consumes once a preemptible quota was granted (<see cref="SpotRegionalMetric"/> decides). Null when there is no metric.</summary>
    private static string? Preemptible(string? metric) => metric is null ? null : "PREEMPTIBLE_" + metric;
}

/// <summary>One quota line: the limit and what is already used (Compute reports both as numbers). A line whose limit the reply did not
/// carry has a null <see cref="Limit"/> and so a null <see cref="Remaining"/>: UNKNOWN for that metric, never zero and never absent.
/// </summary>
public sealed record QuotaMetric(string Metric, double? Limit, double Usage)
{
    public double? Remaining => Limit is { } limit ? Math.Max(0, limit - Usage) : null;
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
    /// A region present without the metric offers none of it; a region the read did not return, or a metric line with no limit, is unknown.
    /// </summary>
    public double? GpuRemaining(string region, string regionalMetric)
    {
        double? regional = null;
        if (Regions is not null)
        {
            var match = Regions.FirstOrDefault(r => string.Equals(r.Key, region, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
            {
                var line = match.Value.FirstOrDefault(m => string.Equals(m.Metric, regionalMetric, StringComparison.Ordinal));

                // No line at all is a region that offers none of it (0); a line without a limit is unknown (null).
                regional = line is null ? 0 : line.Remaining;
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
