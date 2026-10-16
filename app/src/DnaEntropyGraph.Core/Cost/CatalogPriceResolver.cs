using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>A table made from the catalog, or the one reason none could be (for logs and tests, never shown to a user).</summary>
public sealed record CatalogResolveResult(PricingTable? Table, string? Problem);

/// <summary>
/// What one machine type is made of, for pricing: Compute Engine bills a core, a GB of memory and (for these tiers) a GPU as separate SKUs.
/// The shapes are Google's published machine types. <see cref="CoreToken"/> and <see cref="RamToken"/> are the words that name the family in the
/// SKU description; <see cref="GpuToken"/> names the GPU. THEORY (unverified, no live catalog read): the wording matches the live catalog;
/// docs/ToTest.md carries the real-catalog row. A machine whose parts are not found is left to the shipped list, never guessed.
/// </summary>
internal sealed record MachineShape(string MachineType, string Accelerator, int VCpus, int RamGb, string CoreToken, string RamToken, string GpuToken, int Gpus = 1);

/// <summary>
/// Turns catalog SKUs into hourly machine prices and a disk price (issue #214). Pure: a list in, a table out. For each machine it sums
/// cores x core rate + GB x memory rate + GPUs x GPU rate, on demand and for Spot (usage type <c>Preemptible</c>). A component that is missing,
/// not positive, or priced two different ways makes that price unknown: Spot becomes null, an on-demand price keeps the shipped row. The
/// result is only usable with a disk price and at least one machine from the catalog.
/// </summary>
public static class CatalogPriceResolver
{
    private const string OnDemand = "OnDemand";
    private const string Spot = "Preemptible";

    private static readonly string[] Excluded = ["commitment", "custom", "sole tenancy", "reserved", "reservation", "regional", "snapshot", "flex", "dws"];

    private static readonly MachineShape[] Shapes =
    [
        new("g2-standard-8", "1x NVIDIA L4", 8, 32, "G2 Instance Core", "G2 Instance Ram", "Nvidia L4 GPU"),
        new("a2-highgpu-1g", "1x NVIDIA A100 40GB", 12, 85, "A2 Instance Core", "A2 Instance Ram", "Nvidia Tesla A100 GPU"),
        new("a2-ultragpu-1g", "1x NVIDIA A100 80GB", 12, 170, "A2 Ultra Instance Core", "A2 Ultra Instance Ram", "Nvidia Tesla A100 80GB GPU"),
    ];

    public static CatalogResolveResult Resolve(IReadOnlyList<CatalogSku> skus, PricingTable shipped, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(skus);
        ArgumentNullException.ThrowIfNull(shipped);

        var region = shipped.Region;
        var inRegion = skus.Where(s => s.UsdPerUnit > 0 && s.Regions.Contains(region, StringComparer.OrdinalIgnoreCase)).ToList();

        var disk = Single(inRegion.Where(s => s.ResourceFamily == "Storage" && s.UsageType == OnDemand && s.UsageUnit == "GBy.mo" && Named(s, "Balanced PD Capacity")));
        if (disk is not { } diskPrice)
        {
            return new CatalogResolveResult(null, "No single zonal Balanced PD Capacity price in " + region + ".");
        }

        var machines = new List<MachinePrice>();
        var kept = new List<string>();
        foreach (var shape in Shapes)
        {
            var onDemand = Price(inRegion, shape, OnDemand);
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

            var spot = Price(inRegion, shape, Spot);
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
            + (kept.Count > 0 ? " Kept from the list that ships with the app (the catalog did not price them): " + string.Join(", ", kept) + "." : string.Empty);
        return new CatalogResolveResult(PricingTable.Create(asOf, region, source, diskPrice, machines), null);
    }

    private static double? Price(List<CatalogSku> skus, MachineShape shape, string usage)
    {
        var core = Single(skus.Where(s => s.ResourceFamily == "Compute" && s.UsageType == usage && s.UsageUnit == "h" && Named(s, shape.CoreToken + " running")));
        var ram = Single(skus.Where(s => s.ResourceFamily == "Compute" && s.UsageType == usage && s.UsageUnit == "GBy.h" && Named(s, shape.RamToken + " running")));
        var gpu = Single(skus.Where(s => s.ResourceFamily == "Compute" && s.UsageType == usage && s.UsageUnit == "h" && Named(s, shape.GpuToken + " running")));
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
