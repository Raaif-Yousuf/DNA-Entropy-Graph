using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #88 (quota read): the real <see cref="IQuotaGateway"/> over the scripted HTTP handler: <c>regions.list</c> for the
/// per-region metrics and <c>projects.get</c> for the project-wide GPUS_ALL_REGIONS cap. Payloads are Google's documented
/// Compute v1 shapes, not captured from a live project (no GCP access in this build; docs/ToTest.md carries the real-project row).
/// </summary>
public class GoogleQuotaGatewayTests
{
    private const string Regions = "/compute/v1/projects/my-lab/regions";
    private const string Project = "/compute/v1/projects/my-lab";

    private const CloudErrorKind ExpectedKindOf500 = CloudErrorKind.Network;

    private static string Quota(string metric, double limit, double usage = 0)
        => "{\"metric\":\"" + metric + "\",\"limit\":" + limit + ",\"usage\":" + usage + "}";

    private static string Region(string name, params string[] quotas)
        => "{\"kind\":\"compute#region\",\"name\":\"" + name + "\",\"quotas\":[" + string.Join(",", quotas) + "]}";

    private static string ProjectBody(params string[] quotas)
        => "{\"kind\":\"compute#project\",\"name\":\"my-lab\",\"quotas\":[" + string.Join(",", quotas) + "]}";

    private static string Error(int status)
        => "{\"error\":{\"code\":" + status + ",\"message\":\"" + (status == 403 ? "Required 'compute.regions.list' permission for 'projects/my-lab'" : "Internal error.") + "\",\"status\":\"" + (status == 403 ? "PERMISSION_DENIED" : "INTERNAL") + "\"}}";

    private static GoogleGatewayHarness Scripted(double allRegions, double l4InCentral = 1, double l4Used = 0)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            HttpMethod.Get,
            Regions,
            200,
            "{\"items\":[" + Region("us-central1", Quota("NVIDIA_L4_GPUS", l4InCentral, l4Used), Quota("CPUS", 24, 8), Quota("SSD_TOTAL_GB", 500, 150))
                + "," + Region("europe-west4", Quota("NVIDIA_L4_GPUS", 0)) + "]}");
        rig.Handler.Returns(HttpMethod.Get, Project, 200, ProjectBody(Quota("GPUS_ALL_REGIONS", allRegions), Quota("CPUS_ALL_REGIONS", 32)));
        return rig;
    }

    [Fact]
    public async Task ReadQuota_parses_the_regional_table_with_cpu_ssd_and_gpu_metrics_and_the_global_cap()
    {
        var rig = Scripted(allRegions: 1);

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        result.Failures.ShouldBeEmpty();
        var central = result.Regions!["us-central1"].ToDictionary(m => m.Metric);
        central[QuotaMetrics.NvidiaL4Gpus].Limit.ShouldBe(1);
        central["CPUS"].Usage.ShouldBe(8);
        central["SSD_TOTAL_GB"].Limit.ShouldBe(500);
        result.Regions.Keys.ShouldBe(["us-central1", "europe-west4"], ignoreOrder: true);
        result.AllRegionsGpus!.Limit.ShouldBe(1);
        rig.Handler.Requests.ShouldAllBe(r => r.Authorization == "Bearer " + rig.Tokens.CurrentToken);
    }

    [Fact]
    public async Task A_regional_one_with_a_global_zero_reads_as_no_usable_quota_through_the_public_interface()
    {
        var rig = Scripted(allRegions: 0, l4InCentral: 1);

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.None);
    }

    [Fact]
    public async Task GetGpuQuota_returns_the_smaller_of_regional_remaining_and_the_global_cap()
    {
        var rig = Scripted(allRegions: 4, l4InCentral: 3, l4Used: 1);

        var available = await rig.Gateways.Quota.GetGpuQuotaAsync("my-lab", "us-central1", "nvidia-l4", CancellationToken.None);

        available.ShouldBe(2);
    }

    [Fact]
    public async Task GetGpuQuota_with_a_global_zero_returns_zero_for_a_region_that_has_one()
    {
        var rig = Scripted(allRegions: 0, l4InCentral: 1);

        (await rig.Gateways.Quota.GetGpuQuotaAsync("my-lab", "us-central1", "gpu", CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task Regions_list_follows_the_next_page_token_and_merges_every_page()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(HttpMethod.Get, Regions, 200, "{\"items\":[" + Region("us-central1", Quota("NVIDIA_L4_GPUS", 1)) + "],\"nextPageToken\":\"page2\"}");
        rig.Handler.Returns(HttpMethod.Get, Regions, 200, "{\"items\":[" + Region("us-east1", Quota("NVIDIA_L4_GPUS", 2)) + "]}");
        rig.Handler.Returns(HttpMethod.Get, Project, 200, ProjectBody(Quota("GPUS_ALL_REGIONS", 8)));

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        result.Regions!.Keys.ShouldBe(["us-central1", "us-east1"], ignoreOrder: true);
        var calls = rig.Handler.To(HttpMethod.Get, Regions);
        calls.Count.ShouldBe(2);
        calls[0].Uri.Query.ShouldNotContain("pageToken");
        calls[1].Uri.Query.ShouldContain("pageToken=page2");
    }

    [Fact]
    public async Task A_repeated_page_token_is_a_failed_read_not_a_partial_table()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Regions, 200, "{\"items\":[" + Region("us-central1", Quota("NVIDIA_L4_GPUS", 1)) + "],\"nextPageToken\":\"same\"}");
        rig.Handler.Returns(HttpMethod.Get, Regions, 200, "{\"items\":[],\"nextPageToken\":\"same\"}");
        rig.Handler.Returns(HttpMethod.Get, Regions, 200, "{\"items\":[],\"nextPageToken\":\"same\"}");
        rig.Handler.Returns(HttpMethod.Get, Project, 200, ProjectBody(Quota("GPUS_ALL_REGIONS", 1)));

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        result.Regions.ShouldBeNull();
        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.Unknown);
        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Call.ShouldBe("regions.list");
        failure.Error.Code.ShouldBe("LIST_PAGINATION_STUCK");
        failure.Kind.ShouldBe(CloudErrorKind.Other, "a repeated page token is not a connection problem");
    }

    [Fact]
    public async Task A_quota_line_with_no_limit_is_unknown_for_that_metric_not_zero_and_not_absent()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            HttpMethod.Get,
            Regions,
            200,
            "{\"items\":[" + Region("us-central1", "{\"metric\":\"NVIDIA_L4_GPUS\",\"usage\":0}") + "," + Region("europe-west4", "{\"metric\":\"NVIDIA_L4_GPUS\",\"usage\":0}") + "]}");
        rig.Handler.Returns(HttpMethod.Get, Project, 200, ProjectBody(Quota("GPUS_ALL_REGIONS", 1)));
        var other = Scripted(allRegions: 0);

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        var line = result.Regions!["us-central1"].ShouldHaveSingleItem();
        line.Metric.ShouldBe(QuotaMetrics.NvidiaL4Gpus, "the metric must stay in the table, not vanish as if the region offered none");
        line.Limit.ShouldBeNull();
        line.Remaining.ShouldBeNull();
        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.Unknown);
        await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Quota.GetGpuQuotaAsync("my-lab", "us-central1", "nvidia-l4", CancellationToken.None));

        // A definite zero elsewhere still wins: a global cap of 0 binds whatever the unreadable regional line says.
        (await other.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None)).GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.None);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task A_failed_regions_read_is_unknown_never_zero(int status)
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Regions, status, Error(status));
        rig.Handler.Returns(HttpMethod.Get, Project, 200, ProjectBody(Quota("GPUS_ALL_REGIONS", 1)));

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        result.Regions.ShouldBeNull();
        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.Unknown);
        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Call.ShouldBe("regions.list");
        failure.Error.HttpStatus.ShouldBe(status);
        failure.Kind.ShouldBe(status == 403 ? CloudErrorKind.Permission : ExpectedKindOf500);
    }

    [Fact]
    public async Task A_failed_project_read_leaves_a_positive_region_unknown_and_a_zero_region_none()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Regions, 200, "{\"items\":[" + Region("us-central1", Quota("NVIDIA_L4_GPUS", 1)) + "," + Region("europe-west4", Quota("NVIDIA_L4_GPUS", 0)) + "]}");
        rig.Handler.Returns(HttpMethod.Get, Project, 500, Error(500));

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        result.AllRegionsGpus.ShouldBeNull();
        result.Failures.Single().Call.ShouldBe("projects.get");
        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.Unknown);
        result.GpuState("europe-west4", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.None);
    }

    [Fact]
    public async Task A_project_reply_with_no_global_gpu_metric_is_unknown_not_zero()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(HttpMethod.Get, Regions, 200, "{\"items\":[" + Region("us-central1", Quota("NVIDIA_L4_GPUS", 1)) + "]}");
        rig.Handler.Returns(HttpMethod.Get, Project, 200, ProjectBody(Quota("CPUS_ALL_REGIONS", 32)));

        var result = await rig.Gateways.Quota.ReadQuotaAsync("my-lab", CancellationToken.None);

        result.AllRegionsGpus.ShouldBeNull();
        result.GpuState("us-central1", QuotaMetrics.NvidiaL4Gpus).ShouldBe(QuotaState.Unknown);
    }

    [Fact]
    public async Task GetGpuQuota_on_an_unreadable_quota_throws_the_classified_error_instead_of_returning_zero()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(HttpMethod.Get, Regions, 403, Error(403));
        rig.Handler.Returns(HttpMethod.Get, Project, 200, ProjectBody(Quota("GPUS_ALL_REGIONS", 1)));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Quota.GetGpuQuotaAsync("my-lab", "us-central1", "nvidia-l4", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task GetGpuQuota_for_an_accelerator_with_no_known_metric_throws_instead_of_guessing()
    {
        var rig = Scripted(allRegions: 1);

        await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Quota.GetGpuQuotaAsync("my-lab", "us-central1", "nvidia-made-up", CancellationToken.None));
    }

    [Fact]
    public async Task The_quota_gateway_is_the_real_one_and_is_not_wrapped_in_a_resilient_decorator_and_answers_through_the_scripted_http()
    {
        var rig = Scripted(allRegions: 1);

        rig.Gateways.Quota.ShouldNotBeOfType<ResilientQuotaGateway>();
        (await rig.Gateways.Quota.GetGpuQuotaAsync("my-lab", "us-central1", "nvidia-l4", CancellationToken.None)).ShouldBe(1);
        rig.Handler.To(HttpMethod.Get, Regions).ShouldNotBeEmpty("the call never reached the scripted HTTP handler, so it did not go through the real gateway");
    }
}
