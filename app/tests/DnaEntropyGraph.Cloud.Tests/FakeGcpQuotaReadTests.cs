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
}
