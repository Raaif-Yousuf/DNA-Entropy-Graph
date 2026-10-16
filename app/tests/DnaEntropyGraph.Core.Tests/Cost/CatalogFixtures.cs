using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Tests.Cost;

/// <summary>
/// SKU lists shaped like the Cloud Billing Catalog API's Compute Engine service (description wording, resource family and group, usage type,
/// service regions). The prices are invented round numbers, NOT recorded from the live catalog (no GCP access in this build): they let a test
/// check the arithmetic by hand. THEORY (unverified): the description wording matches the live catalog; docs/ToTest.md carries the real-catalog row.
/// </summary>
internal static class CatalogFixtures
{
    public const double G2Core = 0.03;
    public const double G2Ram = 0.004;
    public const double L4 = 0.5;
    public const double G2CoreSpot = 0.01;
    public const double G2RamSpot = 0.001;
    public const double L4Spot = 0.1;
    public const double BalancedPd = 0.12;

    public static CatalogSku Sku(string description, string family, string group, string usage, double price, string unit = "h", params string[] regions)
        => new("id-" + description.GetHashCode(), description, family, group, usage, regions.Length == 0 ? ["us-central1"] : regions, price, unit);

    /// <summary>Everything the g2-standard-8 price needs, on-demand and Spot, plus the disk, plus noise a resolver must ignore.</summary>
    public static List<CatalogSku> G2AndDisk() =>
    [
        Sku("G2 Instance Core running in Americas", "Compute", "CPU", "OnDemand", G2Core),
        Sku("G2 Instance Ram running in Americas", "Compute", "RAM", "OnDemand", G2Ram, "GBy.h"),
        Sku("Nvidia L4 GPU running in Americas", "Compute", "L4GPU", "OnDemand", L4),
        Sku("Spot Preemptible G2 Instance Core running in Americas", "Compute", "CPU", "Preemptible", G2CoreSpot),
        Sku("Spot Preemptible G2 Instance Ram running in Americas", "Compute", "RAM", "Preemptible", G2RamSpot, "GBy.h"),
        Sku("Spot Preemptible Nvidia L4 GPU running in Americas", "Compute", "L4GPU", "Preemptible", L4Spot),
        Sku("Balanced PD Capacity", "Storage", "SSD", "OnDemand", BalancedPd, "GBy.mo"),

        // Noise: another region, a commitment, a regional disk, a custom shape.
        Sku("G2 Instance Core running in Frankfurt", "Compute", "CPU", "OnDemand", 9.99, "h", "europe-west3"),
        Sku("Commitment v1: G2 Cpu in Americas for 1 Year", "Compute", "CPU", "Commit1Yr", 9.99),
        Sku("Regional Balanced PD Capacity", "Storage", "SSD", "OnDemand", 9.99, "GBy.mo"),
        Sku("G2 Custom Instance Core running in Americas", "Compute", "CPU", "OnDemand", 9.99),
    ];

    public static double G2OnDemand => (8 * G2Core) + (32 * G2Ram) + L4;

    public static double G2Spot => (8 * G2CoreSpot) + (32 * G2RamSpot) + L4Spot;
}
