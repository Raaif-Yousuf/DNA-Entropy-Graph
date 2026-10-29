using DnaEntropyGraph.Core.Cost;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cost;

/// <summary>Issue #214: catalog SKUs (per core hour, per GB hour, per GPU hour, per GB month) become the hourly machine prices the estimate uses.</summary>
public sealed class CatalogPriceResolverTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    private static PricingTable Shipped() => PricingTable.Parse(PricingTableTests.Good).Table!;

    [Fact]
    public void A_g2_standard_8_is_8_cores_plus_32_gb_plus_one_L4_for_on_demand_and_for_spot()
    {
        var result = CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk(), Shipped(), Today);

        var price = result.Table!.Find("g2-standard-8")!;
        price.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
        price.SpotUsdPerHour.ShouldBe(CatalogFixtures.G2Spot);
    }

    [Fact]
    public void The_disk_price_is_the_zonal_balanced_pd_capacity_per_gb_month()
    {
        CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk(), Shipped(), Today).Table!.DiskUsdPerGbMonth.ShouldBe(CatalogFixtures.BalancedPd);
    }

    [Fact]
    public void The_table_carries_the_fetch_date_and_says_it_came_from_the_catalog()
    {
        var table = CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk(), Shipped(), Today).Table!;

        table.AsOf.ShouldBe(Today);
        table.Source.ShouldContain("Catalog");
        table.Region.ShouldBe(Shipped().Region);
    }

    [Fact]
    public void Skus_of_another_region_a_commitment_a_regional_disk_or_a_custom_shape_are_ignored()
    {
        // The noise rows in the fixture are 9.99; the answer being the clean figure proves none was used.
        CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk(), Shipped(), Today).Table!.Find("g2-standard-8")!.OnDemandUsdPerHour
            .ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
    }

    [Fact]
    public void A_missing_spot_sku_with_no_shipped_spot_either_leaves_spot_unknown_not_free()
    {
        var skus = CatalogFixtures.G2AndDisk().Where(s => s.UsageType != "Preemptible").ToList();

        var noSpotShipped = PricingTable.Parse(PricingTableTests.Good.Replace("\"spotUsdPerHour\": 0.18", "\"spotUsdPerHour\": null")).Table!;

        CatalogPriceResolver.Resolve(skus, noSpotShipped, Today).Table!.Find("g2-standard-8")!.SpotUsdPerHour.ShouldBeNull();
    }

    [Fact]
    public void An_unresolved_spot_price_keeps_the_shipped_spot_price_and_the_source_says_so_not_null()
    {
        var skus = CatalogFixtures.G2AndDisk().Where(s => s.UsageType != "Preemptible").ToList();

        var table = CatalogPriceResolver.Resolve(skus, Shipped(), Today).Table!;

        table.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
        table.Find("g2-standard-8")!.SpotUsdPerHour.ShouldBe(0.18);
        table.Source.ShouldContain("Spot");
        table.Source.ShouldContain("g2-standard-8");
        CatalogPriceResolver.Resolve(skus, Shipped(), Today).SpotKept.ShouldBe(["g2-standard-8"]);
    }

    [Fact]
    public void A_kept_spot_price_above_the_new_on_demand_price_is_dropped_not_kept()
    {
        var skus = CatalogFixtures.G2AndDisk().Where(s => s.UsageType != "Preemptible").Select(s => s with { UsdPerUnit = s.UsdPerUnit * 0.01 }).ToList();
        skus.Add(CatalogFixtures.Sku("Balanced PD Capacity", "Storage", "SSD", "OnDemand", 0.12, "GBy.mo"));
        skus.RemoveAll(s => s.Description == "Balanced PD Capacity" && s.UsdPerUnit < 0.12);

        CatalogPriceResolver.Resolve(skus, Shipped(), Today).Table!.Find("g2-standard-8")!.SpotUsdPerHour.ShouldBeNull();
    }

    [Fact]
    public void The_spot_gpu_sku_worded_attached_to_Spot_Preemptible_VMs_is_found()
    {
        var spotGpu = CatalogFixtures.G2AndDisk().Single(s => s.Description.StartsWith("Nvidia L4 GPU attached to Spot", StringComparison.Ordinal));
        spotGpu.UsageType.ShouldBe("Preemptible");

        CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk(), Shipped(), Today).Table!.Find("g2-standard-8")!.SpotUsdPerHour
            .ShouldBe(CatalogFixtures.G2Spot);
    }

    [Fact]
    public void A_machine_the_catalog_does_not_price_keeps_its_shipped_row_and_the_source_names_it()
    {
        var table = CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk(), Shipped(), Today).Table!;

        table.Find("a2-highgpu-1g")!.OnDemandUsdPerHour.ShouldBe(Shipped().Find("a2-highgpu-1g")!.OnDemandUsdPerHour);
        table.Source.ShouldContain("a2-highgpu-1g");
    }

    [Fact]
    public void Two_skus_that_disagree_for_one_component_leave_that_machine_unpriced_never_a_guess()
    {
        var skus = CatalogFixtures.G2AndDisk();
        skus.Add(CatalogFixtures.Sku("Nvidia L4 GPU running in Americas", "Compute", "L4GPU", "OnDemand", 7.0));

        // The only machine the catalog could price was g2, so nothing came from the catalog: the caller keeps the whole shipped list.
        var result = CatalogPriceResolver.Resolve(skus, Shipped(), Today);

        result.Table.ShouldBeNull();
        result.Problem.ShouldNotBeNull();
    }

    [Fact]
    public void A_catalog_with_no_machine_resolved_is_a_problem_so_the_caller_falls_back()
    {
        CatalogPriceResolver.Resolve([], Shipped(), Today).Table.ShouldBeNull();
    }

    [Fact]
    public void A_missing_disk_price_keeps_the_shipped_disk_price_and_does_not_throw_the_machines_away()
    {
        // Issue #214 review M2: one piece the catalog cannot give must not cost the pieces it can.
        var result = CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk().Where(s => s.ResourceFamily != "Storage").ToList(), Shipped(), Today);

        result.Table!.DiskUsdPerGbMonth.ShouldBe(Shipped().DiskUsdPerGbMonth);
        result.Table.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
        result.Table.Source.ShouldContain("disk");
    }

    [Fact]
    public void Two_in_region_disk_skus_at_different_prices_keep_the_shipped_disk_price_only()
    {
        var skus = CatalogFixtures.G2AndDisk();
        skus.Add(CatalogFixtures.Sku("Balanced PD Capacity", "Storage", "SSD", "OnDemand", 0.99, "GBy.mo"));

        var result = CatalogPriceResolver.Resolve(skus, Shipped(), Today);

        result.Table!.DiskUsdPerGbMonth.ShouldBe(Shipped().DiskUsdPerGbMonth);
        result.Table.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
        result.Table.Source.ShouldContain("disk");
    }

    [Fact]
    public void An_ambiguous_core_sku_for_one_family_loses_only_that_family_and_the_other_still_comes_from_the_catalog()
    {
        // g2 is priced; the A2 core has two SKUs at different prices, so a2-highgpu keeps its shipped row and the source names it.
        var skus = CatalogFixtures.G2AndDisk();
        skus.AddRange(CatalogFixtures.A2Parts());
        skus.Add(CatalogFixtures.Sku("A2 Instance Core running in Americas", "Compute", "CPU", "OnDemand", 0.77));

        var table = CatalogPriceResolver.Resolve(skus, Shipped(), Today).Table!;

        table.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.G2OnDemand, 1e-9);
        table.Find("a2-highgpu-1g")!.OnDemandUsdPerHour.ShouldBe(Shipped().Find("a2-highgpu-1g")!.OnDemandUsdPerHour);
        table.Source.ShouldContain("a2-highgpu-1g");
    }

    [Fact]
    public void An_a2_ultra_is_never_priced_from_the_catalog_while_its_shape_is_only_a_theory()
    {
        // Issue #214 review M3: a2-highgpu-1g is priced from the A2 SKUs; a2-ultragpu-1g (80GB, THEORY) keeps the shipped row.
        var skus = CatalogFixtures.G2AndDisk();
        skus.AddRange(CatalogFixtures.A2Parts());

        var shipped = PricingTable.Parse(PricingTableTests.Good.Replace(
            "\"a2-highgpu-1g\"",
            "\"a2-ultragpu-1g\": { \"accelerator\": \"1x NVIDIA A100 80GB\", \"onDemandUsdPerHour\": 5.05, \"spotUsdPerHour\": null }, \"a2-highgpu-1g\"")).Table!;

        var table = CatalogPriceResolver.Resolve(skus, shipped, Today).Table!;

        table.Find("a2-highgpu-1g")!.OnDemandUsdPerHour.ShouldBe(CatalogFixtures.A2HighOnDemand, 1e-9);
        table.Find("a2-ultragpu-1g")!.OnDemandUsdPerHour.ShouldBe(5.05);
        table.Source.ShouldContain("a2-ultragpu-1g");
    }

    [Fact]
    public void A_non_positive_price_is_never_used()
    {
        var skus = CatalogFixtures.G2AndDisk();
        skus[2] = CatalogFixtures.Sku("Nvidia L4 GPU running in Americas", "Compute", "L4GPU", "OnDemand", 0);

        CatalogPriceResolver.Resolve(skus, Shipped(), Today).Table.ShouldBeNull();
    }

    [Fact]
    public void A_resolved_table_survives_a_round_trip_through_its_own_json()
    {
        var table = CatalogPriceResolver.Resolve(CatalogFixtures.G2AndDisk(), Shipped(), Today).Table!;

        var back = PricingTable.Parse(table.ToJson()).Table!;

        back.AsOf.ShouldBe(table.AsOf);
        back.Source.ShouldBe(table.Source);
        back.DiskUsdPerGbMonth.ShouldBe(table.DiskUsdPerGbMonth);
        back.Find("g2-standard-8")!.SpotUsdPerHour.ShouldBe(table.Find("g2-standard-8")!.SpotUsdPerHour);
        back.Find("a2-highgpu-1g")!.SpotUsdPerHour.ShouldBeNull();
    }
}
