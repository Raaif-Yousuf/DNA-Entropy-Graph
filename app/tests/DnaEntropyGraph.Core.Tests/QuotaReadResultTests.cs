using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #88: the three-way quota answer (unknown, none, available) and the rule that the project-wide GPUS_ALL_REGIONS cap
/// overrides a positive regional number (CLAUDE.md Critical Pitfalls). A read that failed is UNKNOWN and never zero.
/// </summary>
public class QuotaReadResultTests
{
    private const string L4 = QuotaMetrics.NvidiaL4Gpus;

    private static readonly QuotaReadFailure Denied = new("regions.list", new CloudError("PERMISSION_DENIED", 403, "denied"), CloudErrorKind.Permission);

    private static QuotaReadResult Table(double regionalLimit, double regionalUsage, double? allRegionsLimit, string region = "us-central1")
        => new(
            new Dictionary<string, IReadOnlyList<QuotaMetric>>(StringComparer.OrdinalIgnoreCase)
            {
                [region] = [new QuotaMetric(L4, regionalLimit, regionalUsage), new QuotaMetric("CPUS", 24, 0)],
            },
            allRegionsLimit is { } cap ? new QuotaMetric(QuotaMetrics.GpusAllRegions, cap, 0) : null,
            []);

    [Fact]
    public void Positive_regional_and_positive_global_is_available()
        => Table(1, 0, 1).GpuState("us-central1", L4).ShouldBe(QuotaState.Available);

    [Fact]
    public void A_global_cap_of_zero_overrides_a_regional_one()
    {
        var result = Table(1, 0, 0);

        result.GpuState("us-central1", L4).ShouldBe(QuotaState.None);
        result.GpuRemaining("us-central1", L4).ShouldBe(0);
    }

    [Fact]
    public void A_regional_limit_of_zero_is_none_even_when_the_global_cap_is_positive()
        => Table(0, 0, 4).GpuState("us-central1", L4).ShouldBe(QuotaState.None);

    [Fact]
    public void Usage_is_subtracted_so_a_fully_used_limit_is_none()
        => Table(1, 1, 1).GpuState("us-central1", L4).ShouldBe(QuotaState.None);

    [Fact]
    public void A_region_that_lists_no_such_metric_offers_none_of_it()
        => Table(1, 0, 1).GpuState("us-central1", QuotaMetrics.NvidiaA100Gpus).ShouldBe(QuotaState.None);

    [Fact]
    public void A_region_the_read_did_not_return_is_unknown_never_none()
        => Table(1, 0, 1).GpuState("europe-west4", L4).ShouldBe(QuotaState.Unknown);

    [Fact]
    public void Region_names_compare_case_insensitively()
        => Table(1, 0, 1).GpuState("US-Central1", L4).ShouldBe(QuotaState.Available);

    [Fact]
    public void A_failed_regions_read_is_unknown_never_none()
    {
        var failed = new QuotaReadResult(null, new QuotaMetric(QuotaMetrics.GpusAllRegions, 1, 0), [Denied]);

        failed.GpuState("us-central1", L4).ShouldBe(QuotaState.Unknown);
        failed.GpuRemaining("us-central1", L4).ShouldBeNull();
    }

    [Fact]
    public void A_failed_global_read_with_a_positive_region_is_unknown()
        => Table(1, 0, null).GpuState("us-central1", L4).ShouldBe(QuotaState.Unknown);

    [Fact]
    public void A_failed_global_read_with_a_zero_region_is_still_none()
        => Table(0, 0, null).GpuState("us-central1", L4).ShouldBe(QuotaState.None);

    [Fact]
    public void Remaining_is_the_smaller_of_the_two_and_never_negative()
    {
        Table(4, 1, 2).GpuRemaining("us-central1", L4).ShouldBe(2);
        Table(1, 3, 2).GpuRemaining("us-central1", L4).ShouldBe(0);
    }

    [Fact]
    public void Failed_builds_a_result_that_is_unknown_everywhere()
    {
        var failed = QuotaReadResult.Failed(Denied);

        failed.Failures.ShouldHaveSingleItem();
        failed.GpuState("us-central1", L4).ShouldBe(QuotaState.Unknown);
    }

    [Theory]
    [InlineData("gpu", QuotaMetrics.NvidiaL4Gpus)]
    [InlineData("nvidia-l4", QuotaMetrics.NvidiaL4Gpus)]
    [InlineData("NVIDIA-L4", QuotaMetrics.NvidiaL4Gpus)]
    [InlineData("nvidia-tesla-a100", QuotaMetrics.NvidiaA100Gpus)]
    [InlineData("nvidia-a100-80gb", QuotaMetrics.NvidiaA100Gpus80Gb)]
    [InlineData("NVIDIA_L4_GPUS", QuotaMetrics.NvidiaL4Gpus)]
    public void An_accelerator_type_maps_to_its_regional_quota_metric(string acceleratorType, string metric)
        => QuotaMetrics.ForAccelerator(acceleratorType).ShouldBe(metric);

    [Fact]
    public void An_accelerator_type_nobody_knows_has_no_metric()
        => QuotaMetrics.ForAccelerator("nvidia-made-up").ShouldBeNull();
}
