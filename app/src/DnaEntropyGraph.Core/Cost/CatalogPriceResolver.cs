using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>A table made from the catalog, or the one reason none could be (for logs and tests, never shown to a user).</summary>
public sealed record CatalogResolveResult(PricingTable? Table, string? Problem, IReadOnlyList<string>? SpotKept = null);

/// <summary>
/// What one machine type is made of, for pricing: Compute Engine bills a core, a GB of memory and (for these tiers) a GPU as separate SKUs.
/// The shapes are Google's published machine types. <see cref="CoreToken"/> and <see cref="RamToken"/> are the words that name the family in the
/// SKU description; <see cref="GpuToken"/> names the GPU. MEASURED 2026-10-16 (public SKU explorer, docs/cloud_design.md section 10): the descriptions are
/// "G2 Instance Core running in Americas", "Spot Preemptible G2 Instance Ram running in Americas", "Nvidia L4 GPU running in Americas" and
/// "Nvidia L4 GPU attached to Spot Preemptible VMs running in Americas", so a GPU is matched by name alone (the usage type tells Spot from on demand).
/// THEORY (unverified): a2-ultragpu-1g is billed from the A2 core and memory SKUs plus the 80GB GPU (the explorer lists no A2 Ultra core or memory SKU).
/// Until that is measured the shape has <c>CatalogPriced</c> false: its shipped row is kept and the source says so, never a theory-priced row labelled as the catalog's.
/// A machine whose parts are not found is left to the shipped list, never guessed.
/// </summary>
internal sealed record MachineShape(string MachineType, string Accelerator, int VCpus, int RamGb, string CoreToken, string RamToken, string GpuToken, int Gpus = 1, bool CatalogPriced = true);

/// <summary>
/// Turns catalog SKUs into hourly machine prices and a disk price (issue #214). Pure: a list in, a table out. For each machine it sums
/// cores x core rate + GB x memory rate + GPUs x GPU rate, on demand and for Spot (usage type <c>Preemptible</c>). A component that is missing,
/// not positive, or priced two different ways makes that price unknown: Spot becomes null, an on-demand price keeps the shipped row. The
/// result is only usable with a disk price and at least one machine from the catalog.
/// </summary>
public static class CatalogPriceResolver
{
    /// <summary>
    /// Bump this whenever the matching rules or the machine shapes change: it is part of the cache fingerprint, so an app update that
    /// changes what the resolver makes of the same SKUs does not keep serving today's cache. History: 1 first version, 2 real SKU wording
    /// (GPU matched by name, Spot kept from the shipped list), 3 the kept-Spot list is recorded,
    /// 4 a2-ultragpu-1g is no longer priced from the catalog (theory) and a disk price the catalog cannot give keeps the shipped one.
    /// </summary>
    public const int Version = 4;

    private const string OnDemand = "OnDemand";
    private const string Spot = "Preemptible";

    private static readonly string[] Excluded = ["commitment", "custom", "sole tenancy", "reserved", "reservation", "regional", "snapshot", "flex", "dws"];

    private static readonly MachineShape[] Shapes =
    [
        new("g2-standard-8", "1x NVIDIA L4", 8, 32, "G2 Instance Core", "G2 Instance Ram", "Nvidia L4 GPU"),
        new("a2-highgpu-1g", "1x NVIDIA A100 40GB", 12, 85, "A2 Instance Core", "A2 Instance Ram", "Nvidia Tesla A100 GPU"),
        new("a2-ultragpu-1g", "1x NVIDIA A100 80GB", 12, 170, "A2 Instance Core", "A2 Instance Ram", "Nvidia Tesla A100 80GB GPU", CatalogPriced: false),
    ];

    public static CatalogResolveResult Resolve(IReadOnlyList<CatalogSku> skus, PricingTable shipped, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(skus);
        ArgumentNullException.ThrowIfNull(shipped);

        var region = shipped.Region;
        var inRegion = skus.Where(s => s.UsdPerUnit > 0 && s.Regions.Contains(region, StringComparer.OrdinalIgnoreCase)).ToList();

        // A disk price the catalog cannot give (missing, or two prices) keeps the shipped one; it does not cost the machines their own prices.
        var disk = Single(inRegion.Where(s => s.ResourceFamily == "Storage" && s.UsageType == OnDemand && s.UsageUnit == "GBy.mo" && Named(s, "Balanced PD Capacity")));
        var diskFromShipped = disk is null;
        var diskPrice = disk ?? shipped.DiskUsdPerGbMonth;

        var machines = new List<MachinePrice>();
        var kept = new List<string>();
        var spotKept = new List<string>();
        foreach (var shape in Shapes)
        {
            var onDemand = shape.CatalogPriced ? Price(inRegion, shape, OnDemand) : null;
            var fromShipped = shipped.Find(shape.MachineType);
            if (onDemand is null)
            {
                if (fromShipped is not null)
                {
                    machines.Add(fromShipped);
                    kept.Add(shape.MachineType);
                }

                continue;
            }

            // A Spot price the catalog did not give is not "no Spot price": the previous list's one is kept (and named), unless it is now above on demand.
            var spot = Price(inRegion, shape, Spot);
            if (spot is null && fromShipped?.SpotUsdPerHour is { } previous)
            {
                spot = previous;
                spotKept.Add(shape.MachineType);
            }

            machines.Add(new MachinePrice(shape.MachineType, shape.Accelerator, onDemand.Value, spot is { } s && s <= onDemand.Value ? s : null));
        }

        // Machines the shipped list prices that this code has no shape for stay as they were.
        foreach (var other in shipped.Machines.Where(m => Shapes.All(s => s.MachineType != m.MachineType)))
        {
            machines.Add(other);
            kept.Add(other.MachineType);
        }

        if (machines.Count == kept.Count)
        {
            return new CatalogResolveResult(null, "The catalog priced none of the machines.");
        }

        var source = $"Cloud Billing Catalog API, {region}, read {asOf:yyyy-MM-dd}. An estimate, not a quote."
            + (kept.Count > 0 ? " Kept from the list that ships with the app (the catalog did not price them): " + string.Join(", ", kept) + "." : string.Empty)
            + (diskFromShipped ? " The disk price is kept from the list that ships with the app (the catalog gave no single price for it)." : string.Empty)
            + (spotKept.Count > 0 ? " Spot price kept from the list that ships with the app (the catalog gave none): " + string.Join(", ", spotKept) + "." : string.Empty);
        return new CatalogResolveResult(PricingTable.Create(asOf, region, source, diskPrice, machines), null, spotKept.Count > 0 ? spotKept : null);
    }

    private static double? Price(List<CatalogSku> skus, MachineShape shape, string usage)
    {
        var core = Single(skus.Where(s => s.ResourceFamily == "Compute" && s.UsageType == usage && s.UsageUnit == "h" && Named(s, shape.CoreToken + " running")));
        var ram = Single(skus.Where(s => s.ResourceFamily == "Compute" && s.UsageType == usage && s.UsageUnit == "GBy.h" && Named(s, shape.RamToken + " running")));
        var gpu = Single(skus.Where(s => s.ResourceFamily == "Compute" && s.UsageType == usage && s.UsageUnit == "h" && Named(s, shape.GpuToken)));
        return core is { } c && ram is { } r && gpu is { } g ? (shape.VCpus * c) + (shape.RamGb * r) + (shape.Gpus * g) : null;
    }

    private static bool Named(CatalogSku sku, string token)
        => sku.Description.Contains(token, StringComparison.OrdinalIgnoreCase)
            && !Excluded.Any(word => sku.Description.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The one price all matches agree on, or null when there are none or they disagree (never a pick between two numbers).</summary>
    private static double? Single(IEnumerable<CatalogSku> matches)
    {
        var prices = matches.Select(m => m.UsdPerUnit).Distinct().ToList();
        return prices.Count == 1 ? prices[0] : null;
    }
}
