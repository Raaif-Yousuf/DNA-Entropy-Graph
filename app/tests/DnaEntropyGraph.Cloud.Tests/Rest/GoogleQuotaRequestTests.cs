using System.Text.Json;
using System.Web;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #99: the eligibility read (<c>quotaInfos.get</c>) and the request (<c>quotaPreferences.create</c>) of the Cloud Quotas API over a
/// scripted HTTP handler. Payloads are the documented v1 REST shapes, not captured from a live project (no GCP access in this build; docs/ToTest.md
/// carries the row). The decisive observable is the request itself: the path, the query id, and the body that names the metric, the region and the value.
/// </summary>
public class GoogleQuotaRequestTests
{
    private const string Base = "/v1/projects/my-lab/locations/global";
    private const string L4Info = Base + "/services/compute.googleapis.com/quotaInfos/NVIDIA-L4-GPUS-per-project-region";
    private const string AllInfo = Base + "/services/compute.googleapis.com/quotaInfos/GPUS-ALL-REGIONS-per-project";
    private const string Preferences = Base + "/quotaPreferences";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;

    private static string RpcError(int http, string status, string message, string? reason = null)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\""
            + (reason is null ? string.Empty : ",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"" + reason + "\"}]") + "}}";

    private static string Info(string? eligibility) => "{\"name\":\"projects/123/locations/global/services/compute.googleapis.com/quotaInfos/x\",\"quotaId\":\"x\"" + (eligibility is null ? string.Empty : ",\"quotaIncreaseEligibility\":" + eligibility) + "}";

    private static QuotaIncreaseRequest L4Request() => new(QuotaMetrics.NvidiaL4Gpus, "us-central1", 1, "deg-nvidia-l4-gpus-us-central1-20261015", "Evo 2 inference for lab research.", "lab@example.org");

    private static QuotaIncreaseRequest AllRequest() => new(QuotaMetrics.GpusAllRegions, null, 1, "deg-gpus-all-regions-20261015", "Evo 2 inference for lab research.", null);

    // ---------------------------------------------------------------- eligibility

    [Theory]
    [InlineData(QuotaMetrics.NvidiaL4Gpus, L4Info)]
    [InlineData(QuotaMetrics.GpusAllRegions, AllInfo)]
    public async Task Eligibility_reads_the_quota_info_of_that_metric_with_the_account_token(string metric, string path)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, path, 200, Info("{\"isEligible\":true}"));

        var eligibility = await rig.Gateways.Quota.GetQuotaIncreaseEligibilityAsync("my-lab", metric, CancellationToken.None);

        eligibility.Metric.ShouldBe(metric);
        eligibility.IsEligible.ShouldBe(true);
        eligibility.Reason.ShouldBe(QuotaIneligibilityReason.None);
        var request = rig.Handler.To(Get, path).Single();
        request.Uri.Host.ShouldBe("cloudquotas.googleapis.com");
        request.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
    }

    [Theory]
    [InlineData("NO_VALID_BILLING_ACCOUNT", QuotaIneligibilityReason.NoValidBillingAccount)]
    [InlineData("NOT_SUPPORTED", QuotaIneligibilityReason.NotSupported)]
    [InlineData("NOT_ENOUGH_USAGE_HISTORY", QuotaIneligibilityReason.NotEnoughUsageHistory)]
    [InlineData("INELIGIBILITY_REASON_UNSPECIFIED", QuotaIneligibilityReason.Other)]
    [InlineData("SOMETHING_NEW", QuotaIneligibilityReason.Other)]
    public async Task An_ineligible_answer_carries_the_reason(string wire, QuotaIneligibilityReason expected)
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, L4Info, 200, Info("{\"isEligible\":false,\"ineligibilityReason\":\"" + wire + "\"}"));

        var eligibility = await rig.Gateways.Quota.GetQuotaIncreaseEligibilityAsync("my-lab", QuotaMetrics.NvidiaL4Gpus, CancellationToken.None);

        eligibility.IsEligible.ShouldBe(false);
        eligibility.Reason.ShouldBe(expected);
    }

    [Fact]
    public async Task An_info_with_no_eligibility_field_is_unknown_not_eligible()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, L4Info, 200, Info(null));

        var eligibility = await rig.Gateways.Quota.GetQuotaIncreaseEligibilityAsync("my-lab", QuotaMetrics.NvidiaL4Gpus, CancellationToken.None);

        eligibility.IsEligible.ShouldBeNull();
    }

    [Fact]
    public async Task A_quota_id_Google_does_not_know_is_unknown_with_a_named_detail_not_an_exception()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, L4Info, 404, RpcError(404, "NOT_FOUND", "Quota info not found"));

        var eligibility = await rig.Gateways.Quota.GetQuotaIncreaseEligibilityAsync("my-lab", QuotaMetrics.NvidiaL4Gpus, CancellationToken.None);

        eligibility.IsEligible.ShouldBeNull();
        eligibility.Detail.ShouldBe("QUOTA_INFO_NOT_FOUND");
    }

    [Fact]
    public async Task The_Cloud_Quotas_API_being_off_surfaces_as_api_disabled()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Get, L4Info, 403, RpcError(403, "PERMISSION_DENIED", "Cloud Quotas API has not been used in project 123 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/cloudquotas.googleapis.com/overview?project=123", "SERVICE_DISABLED"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Quota.GetQuotaIncreaseEligibilityAsync("my-lab", QuotaMetrics.NvidiaL4Gpus, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.ApiDisabled);
    }

    [Fact]
    public async Task A_metric_with_no_known_quota_id_is_refused_before_any_request()
    {
        var rig = new GoogleGatewayHarness();

        await Should.ThrowAsync<ArgumentException>(() => rig.Gateways.Quota.GetQuotaIncreaseEligibilityAsync("my-lab", "NVIDIA_MADE_UP_GPUS", CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- request

    [Fact]
    public async Task A_regional_request_posts_the_preference_with_service_quota_id_region_and_value()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, Preferences, 200,
            "{\"name\":\"projects/123/locations/global/quotaPreferences/deg-nvidia-l4-gpus-us-central1-20261015\",\"reconciling\":true,\"quotaConfig\":{\"preferredValue\":\"1\",\"stateDetail\":\"Pending review\"}}");

        var result = await rig.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", L4Request(), CancellationToken.None);

        var post = rig.Handler.To(Post, Preferences).Single();
        post.Uri.Host.ShouldBe("cloudquotas.googleapis.com");
        post.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        HttpUtility.ParseQueryString(post.Uri.Query)["quotaPreferenceId"].ShouldBe("deg-nvidia-l4-gpus-us-central1-20261015");
        using var body = JsonDocument.Parse(post.Body);
        var root = body.RootElement;
        root.GetProperty("service").GetString().ShouldBe("compute.googleapis.com");
        root.GetProperty("quotaId").GetString().ShouldBe("NVIDIA-L4-GPUS-per-project-region");
        root.GetProperty("quotaConfig").GetProperty("preferredValue").GetString().ShouldBe("1");
        root.GetProperty("dimensions").GetProperty("region").GetString().ShouldBe("us-central1");
        root.GetProperty("justification").GetString().ShouldBe("Evo 2 inference for lab research.");
        root.GetProperty("contactEmail").GetString().ShouldBe("lab@example.org");
        result.PreferredValue.ShouldBe(1);
        result.GrantedValue.ShouldBeNull();
        result.Reconciling.ShouldBeTrue();
        result.StateDetail.ShouldBe("Pending review");
        result.Granted.ShouldBeFalse();
        result.PreferenceName.ShouldEndWith("deg-nvidia-l4-gpus-us-central1-20261015");
    }

    [Fact]
    public async Task The_project_wide_request_has_no_dimensions_and_no_contact_email_when_none_is_given()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Post, Preferences, 200, "{\"name\":\"n\",\"quotaConfig\":{\"preferredValue\":\"1\",\"grantedValue\":\"1\"}}");

        var result = await rig.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", AllRequest(), CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Post, Preferences).Single().Body);
        body.RootElement.GetProperty("quotaId").GetString().ShouldBe("GPUS-ALL-REGIONS-per-project");
        body.RootElement.TryGetProperty("dimensions", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("contactEmail", out _).ShouldBeFalse();
        result.GrantedValue.ShouldBe(1);
        result.Granted.ShouldBeTrue("a granted value at or above the preferred one is a grant");
    }

    [Fact]
    public async Task A_repeat_of_the_same_preference_id_is_an_already_exists_error()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Post, Preferences, 409, RpcError(409, "ALREADY_EXISTS", "Requested entity already exists"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", L4Request(), CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task A_request_is_one_post_a_failure_after_it_is_not_replayed_by_us()
    {
        var rig = new GoogleGatewayHarness(retries: 0);
        rig.Handler.Returns(Post, Preferences, 403, RpcError(403, "PERMISSION_DENIED", "Permission 'cloudquotas.quotaPreferences.create' denied on resource (or it may not exist)."));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", L4Request(), CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.To(Post, Preferences).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("us-central1", 0)]
    public async Task A_request_with_a_missing_preference_id_or_a_non_positive_value_is_refused_before_any_request(string preferenceId, long value)
    {
        var rig = new GoogleGatewayHarness();
        var request = L4Request() with { PreferenceId = preferenceId, PreferredValue = value };

        await Should.ThrowAsync<ArgumentException>(() => rig.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", request, CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_regional_metric_without_a_region_is_refused_and_a_region_on_the_project_wide_metric_too()
    {
        var rig = new GoogleGatewayHarness();

        await Should.ThrowAsync<ArgumentException>(() => rig.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", L4Request() with { Region = null }, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => rig.Gateways.Quota.RequestQuotaIncreaseAsync("my-lab", AllRequest() with { Region = "us-central1" }, CancellationToken.None));

        rig.Handler.Requests.ShouldBeEmpty();
    }
}
