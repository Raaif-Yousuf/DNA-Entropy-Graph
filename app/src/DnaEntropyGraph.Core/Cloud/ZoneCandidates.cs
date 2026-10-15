namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// What the zone-candidate builder needs (issue #86a). <paramref name="OfferedZones"/> are the zones that offer the accelerator (the
/// <c>acceleratorTypes.aggregatedList</c> answer); <paramref name="RestrictTo"/> narrows them further when not empty (the user's or the
/// ladder's own allowed list). <paramref name="QuotaMetric"/> is the regional metric the run draws on (null for a machine with no GPU
/// metric: nothing is narrowed). <paramref name="Quota"/> is null when no quota was read.
/// </summary>
public sealed record ZoneCandidateInput(
    IReadOnlyList<string> OfferedZones,
    IReadOnlyList<string> RestrictTo,
    string? PreferredZone,
    string? LastGoodZone,
    string? BucketLocation,
    QuotaReadResult? Quota,
    string? QuotaMetric);

/// <summary>The zones to try, in order, and whether the quota read says there is no GPU quota anywhere (the fixable case the user must hear about).</summary>
public sealed record ZoneCandidateResult(IReadOnlyList<string> Zones, bool QuotaZeroEverywhere);

/// <summary>
/// The pure half of the zone escalation ladder (issue #86a), with no gateway: which zones are worth an insert and in what order.
/// The quota rule is the prototype's three-way one (<c>keeper.py::_apply_quota_health</c>): a read that failed, or a region it did not
/// return, is UNKNOWN and never zero; zones are narrowed to regions without a definite zero only when some region definitely has quota;
/// when every zone is a definite zero all of them are still tried (the quota API can under-report) and
/// <see cref="ZoneCandidateResult.QuotaZeroEverywhere"/> carries the guidance.
/// </summary>
public static class ZoneCandidates
{
    public static ZoneCandidateResult Build(ZoneCandidateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var zones = input.OfferedZones
            .Where(z => input.RestrictTo.Count == 0 || input.RestrictTo.Contains(z, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var zeroEverywhere = false;
        if (input.Quota is { } quota && input.QuotaMetric is { } metric && zones.Count > 0)
        {
            var states = zones.Select(z => quota.GpuState(RegionOf(z), metric)).ToList();
            if (states.Contains(QuotaState.Available))
            {
                zones = zones.Where((_, i) => states[i] != QuotaState.None).ToList();
            }
            else
            {
                zeroEverywhere = states.All(s => s == QuotaState.None);
            }
        }

        return new ZoneCandidateResult(Order(zones, input), zeroEverywhere);
    }

    /// <summary>Preferred, then last good (each only when it is a candidate), then the bucket's continent, then the rest, each group in the offered order.</summary>
    private static List<string> Order(List<string> zones, ZoneCandidateInput input)
    {
        var ordered = new List<string>();

        void Add(string? zone)
        {
            var match = zone is null ? null : zones.FirstOrDefault(z => string.Equals(z, zone, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !ordered.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(match);
            }
        }

        Add(input.PreferredZone);
        Add(input.LastGoodZone);
        var continent = ContinentOf(input.BucketLocation);
        if (continent is not null)
        {
            foreach (var zone in zones.Where(z => ContinentOf(z) == continent))
            {
                Add(zone);
            }
        }

        foreach (var zone in zones)
        {
            Add(zone);
        }

        return ordered;
    }

    /// <summary>The accelerator type a machine family carries, or null for a machine without a GPU the app knows.</summary>
    public static string? AcceleratorFor(string machineType)
    {
        var name = machineType?.Trim().ToLowerInvariant() ?? string.Empty;
        return name switch
        {
            _ when name.StartsWith("g2-", StringComparison.Ordinal) => "nvidia-l4",
            _ when name.StartsWith("a2-ultragpu-", StringComparison.Ordinal) => "nvidia-a100-80gb",
            _ when name.StartsWith("a2-", StringComparison.Ordinal) => "nvidia-tesla-a100",
            _ when name.StartsWith("a3-", StringComparison.Ordinal) => "nvidia-h100-80gb",
            _ => null,
        };
    }

    /// <summary>A zone's region: everything before the last dash (<c>us-central1-a</c> is <c>us-central1</c>).</summary>
    public static string RegionOf(string zone)
    {
        var dash = zone.LastIndexOf('-');
        return dash <= 0 ? zone : zone[..dash];
    }

    /// <summary>The continent of a zone, region or bucket location (<c>US</c>, <c>EU</c>, <c>ASIA</c>, <c>europe-west4</c>, <c>us-central1-a</c>), or null when it names none.</summary>
    private static string? ContinentOf(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var text = location.Trim().ToLowerInvariant();
        var head = text.Split('-')[0];
        return head switch
        {
            "us" or "nam4" or "northamerica" => "us",
            "eu" or "europe" or "eur4" => "europe",
            "asia" => "asia",
            _ => head.StartsWith("southamerica", StringComparison.Ordinal) || head.StartsWith("me", StringComparison.Ordinal) || head.StartsWith("africa", StringComparison.Ordinal) || head.StartsWith("australia", StringComparison.Ordinal) ? head : null,
        };
    }
}
