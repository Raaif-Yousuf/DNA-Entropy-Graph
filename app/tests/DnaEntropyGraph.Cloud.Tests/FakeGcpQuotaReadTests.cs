using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #88: <see cref="FakeGcp"/> answers the quota table read with the same three-way logic the real gateway does.</summary>
public class FakeGcpQuotaReadTests
{
    [Fact]
    public async Task The_table_reports_the_configured_regions_and_the_global_cap_so_a_cap_of_zero_overrides_a_regional_one()
    {
        var gcp = new FakeGcp().WithGpuQuota("us-central1", "NVIDIA_L4_GPUS", 1).WithAllRegionsGpuCap(0);

        var result = await gcp.ReadQuotaAsync("p", CancellationToken.None);

        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.None);
    }

    [Fact]
    public async Task A_scripted_read_failure_is_unknown_never_zero()
    {
        var gcp = new FakeGcp().WithGpuQuota("us-central1", "NVIDIA_L4_GPUS", 1).WithQuotaReadFailing(CloudErrorKind.Permission);

        var result = await gcp.ReadQuotaAsync("p", CancellationToken.None);

        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.Unknown);
        result.Failures.ShouldHaveSingleItem().Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission)]
    [InlineData(CloudErrorKind.Network)]
    public async Task A_scripted_read_failure_makes_GetGpuQuota_throw_the_classified_error_like_the_real_gateway_never_return_a_number(CloudErrorKind kind)
    {
        var gcp = new FakeGcp().WithQuotaReadFailing(kind);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.GetGpuQuotaAsync("p", "us-central1", "nvidia-l4", CancellationToken.None));

        ex.Kind.ShouldBe(kind);
        ex.Error.Code.ShouldBe("QUOTA_READ_FAILED");
    }

    [Theory]
    [InlineData("us-central1", "nvidia-l4", null, null)]
    [InlineData("us-east1", "gpu", null, null)]
    [InlineData("europe-west4", "nvidia-l4", 3, null)]
    [InlineData("us-central1", "nvidia-l4", 4, 0)]
    [InlineData("us-central1", "nvidia-tesla-a100", null, null)]
    public async Task The_two_quota_methods_agree_on_a_default_a_scripted_and_a_capped_region(string region, string accelerator, int? scripted, int? cap)
    {
        var gcp = new FakeGcp();
        if (scripted is { } available)
        {
            gcp.WithGpuQuota(region, accelerator, available);
        }

        if (cap is { } ceiling)
        {
            gcp.WithAllRegionsGpuCap(ceiling);
        }

        var metric = QuotaMetrics.ForAccelerator(accelerator)!;
        var table = await gcp.ReadQuotaAsync("p", CancellationToken.None);

        var viaGet = await gcp.GetGpuQuotaAsync("p", region, accelerator, CancellationToken.None);

        table.GpuRemaining(region, metric).ShouldBe(viaGet, "ReadQuotaAsync and GetGpuQuotaAsync disagree");
        viaGet.ShouldBe(cap ?? scripted ?? 1);
    }

    [Fact]
    public async Task A_region_nobody_scripted_is_the_default_of_one_through_GetGpuQuota()
        => (await new FakeGcp().GetGpuQuotaAsync("p", "asia-south1", "nvidia-l4", CancellationToken.None)).ShouldBe(1);
}
