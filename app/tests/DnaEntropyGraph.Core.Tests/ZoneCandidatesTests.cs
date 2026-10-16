using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #86a: the pure zone-candidate builder. The prototype's three-way quota rule (keeper.py _apply_quota_health), the accelerator
/// filter and the ordering (preferred, last good, the bucket's continent, the rest), with no gateway.
/// </summary>
public class ZoneCandidatesTests
{
    private const string L4 = QuotaMetrics.NvidiaL4Gpus;

    private static readonly string[] Offered =
    [
        "us-central1-a", "us-central1-b", "us-east1-b", "europe-west4-a", "europe-west4-b", "asia-east1-a",
    ];

    private static QuotaReadResult Quota(double? allRegions, params (string Region, double? Limit)[] regions)
        => new(
            regions.ToDictionary(r => r.Region, r => (IReadOnlyList<QuotaMetric>)[new QuotaMetric(L4, r.Limit, 0)], StringComparer.OrdinalIgnoreCase),
            allRegions is null ? null : new QuotaMetric(QuotaMetrics.GpusAllRegions, allRegions, 0),
            []);

    private static ZoneCandidateResult Build(
        QuotaReadResult? quota = null,
        IReadOnlyList<string>? offered = null,
        IReadOnlyList<string>? restrictTo = null,
        string? preferred = null,
        string? lastGood = null,
        string? bucketLocation = null,
        string? metric = L4)
        => ZoneCandidates.Build(new ZoneCandidateInput(offered ?? Offered, restrictTo ?? [], preferred, lastGood, bucketLocation, quota, metric));

    [Fact]
    public void With_no_quota_read_every_offered_zone_is_a_candidate_in_offered_order()
    {
        var result = Build(quota: null);

        result.Zones.ShouldBe(Offered);
        result.QuotaZeroEverywhere.ShouldBeFalse();
    }

    [Fact]
    public void A_failed_quota_read_is_unknown_not_zero_so_every_zone_is_tried_and_no_guidance_is_raised()
    {
        var failed = QuotaReadResult.Failed(new QuotaReadFailure("regions.list", new CloudError("X", null, "x"), CloudErrorKind.Network));

        var result = Build(quota: failed);

        result.Zones.ShouldBe(Offered);
        result.QuotaZeroEverywhere.ShouldBeFalse();
    }

    [Fact]
    public void Positive_quota_somewhere_narrows_to_the_regions_with_quota()
    {
        var quota = Quota(1000, ("us-central1", 0), ("us-east1", 1), ("europe-west4", 1), ("asia-east1", 0));

        var result = Build(quota);

        result.Zones.ShouldBe(["us-east1-b", "europe-west4-a", "europe-west4-b"]);
        result.QuotaZeroEverywhere.ShouldBeFalse();
    }

    [Fact]
    public void A_region_the_read_did_not_return_is_unknown_and_stays_a_candidate_beside_a_region_with_quota()
    {
        var quota = Quota(1000, ("us-east1", 1), ("us-central1", 0));

        var result = Build(quota);

        result.Zones.ShouldBe(["us-east1-b", "europe-west4-a", "europe-west4-b", "asia-east1-a"]);
    }

    [Fact]
    public void Zero_everywhere_still_tries_every_zone_and_raises_the_fixable_quota_guidance()
    {
        var quota = Quota(1000, ("us-central1", 0), ("us-east1", 0), ("europe-west4", 0), ("asia-east1", 0));

        var result = Build(quota);

        result.Zones.ShouldBe(Offered);
        result.QuotaZeroEverywhere.ShouldBeTrue();
    }

    [Fact]
    public void A_project_wide_GPUS_ALL_REGIONS_of_zero_counts_as_zero_everywhere_whatever_the_regions_say()
    {
        var quota = Quota(0, ("us-central1", 1), ("us-east1", 1), ("europe-west4", 1), ("asia-east1", 1));

        var result = Build(quota);

        result.Zones.ShouldBe(Offered);
        result.QuotaZeroEverywhere.ShouldBeTrue();
    }

    [Fact]
    public void Zero_in_every_region_that_was_read_but_unknown_elsewhere_is_not_zero_everywhere()
    {
        var quota = Quota(null, ("us-central1", 0), ("us-east1", 0));

        var result = Build(quota);

        result.Zones.ShouldBe(Offered);
        result.QuotaZeroEverywhere.ShouldBeFalse();
    }

    [Fact]
    public void A_zone_without_an_accelerator_metric_is_never_narrowed()
    {
        var quota = Quota(1000, ("us-central1", 0), ("us-east1", 1));

        var result = Build(quota, metric: null);

        result.Zones.ShouldBe(Offered);
    }

    [Fact]
    public void Only_zones_that_offer_the_accelerator_are_candidates()
    {
        var result = Build(offered: ["us-central1-a", "europe-west4-a"], restrictTo: ["us-central1-a", "us-central1-f"]);

        result.Zones.ShouldBe(["us-central1-a"]);
    }

    [Fact]
    public void Nothing_offered_or_nothing_left_after_the_restriction_is_an_empty_list()
    {
        Build(offered: []).Zones.ShouldBeEmpty();
        Build(restrictTo: ["us-central1-f"]).Zones.ShouldBeEmpty();
    }

    [Fact]
    public void The_order_is_preferred_then_last_good_then_the_bucket_continent_then_the_rest()
    {
        var result = Build(preferred: "asia-east1-a", lastGood: "europe-west4-b", bucketLocation: "US");

        result.Zones.ShouldBe(["asia-east1-a", "europe-west4-b", "us-central1-a", "us-central1-b", "us-east1-b", "europe-west4-a"]);
    }

    [Theory]
    [InlineData("EU", "europe-west4-a")]
    [InlineData("europe-west4", "europe-west4-a")]
    [InlineData("ASIA", "asia-east1-a")]
    [InlineData("us-central1", "us-central1-a")]
    [InlineData("US", "us-central1-a")]
    public void The_bucket_location_picks_the_continent_that_goes_first(string location, string expectedFirst)
    {
        Build(bucketLocation: location).Zones[0].ShouldBe(expectedFirst);
    }

    [Fact]
    public void A_preferred_or_last_good_zone_that_is_not_a_candidate_is_dropped_and_never_added()
    {
        var result = Build(preferred: "us-central1-f", lastGood: "nowhere-1-a");

        result.Zones.ShouldBe(Offered);
    }

    [Fact]
    public void A_zone_named_twice_appears_once()
    {
        var result = Build(preferred: "us-east1-b", lastGood: "us-east1-b");

        result.Zones.Distinct().Count().ShouldBe(result.Zones.Count);
        result.Zones[0].ShouldBe("us-east1-b");
    }

    [Fact]
    public void The_preferred_zone_survives_a_quota_narrowing_only_when_its_region_has_quota()
    {
        var quota = Quota(1000, ("us-central1", 0), ("us-east1", 1), ("europe-west4", 1), ("asia-east1", 1));

        Build(quota, preferred: "us-central1-a").Zones.ShouldNotContain("us-central1-a");
        Build(quota, preferred: "us-east1-b").Zones[0].ShouldBe("us-east1-b");
    }

    [Theory]
    [InlineData("g2-standard-8", "nvidia-l4")]
    [InlineData("a2-highgpu-1g", "nvidia-tesla-a100")]
    [InlineData("a2-ultragpu-1g", "nvidia-a100-80gb")]
    [InlineData("a3-highgpu-1g", "nvidia-h100-80gb")]
    [InlineData("e2-standard-4", null)]
    public void The_accelerator_type_follows_the_machine_type(string machineType, string? expected)
        => ZoneCandidates.AcceleratorFor(machineType).ShouldBe(expected);

    [Theory]
    [InlineData("us-central1-a", "us-central1")]
    [InlineData("europe-west4-b", "europe-west4")]
    public void A_zones_region_is_everything_before_the_last_dash(string zone, string region)
        => ZoneCandidates.RegionOf(zone).ShouldBe(region);

    [Fact]
    public void A_Spot_run_reads_the_preemptible_metric_not_the_on_demand_one()
    {
        QuotaMetrics.ForAccelerator("nvidia-l4", spot: true).ShouldBe("PREEMPTIBLE_NVIDIA_L4_GPUS");
        QuotaMetrics.ForAccelerator("nvidia-l4").ShouldBe(L4);
        QuotaMetrics.ForAccelerator("e2", spot: true).ShouldBeNull();
    }
}
