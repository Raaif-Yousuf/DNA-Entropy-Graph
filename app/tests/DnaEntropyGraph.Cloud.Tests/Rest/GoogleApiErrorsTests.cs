using System.Net;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.Requests;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #54 (round 3): the error reasons Cloud Storage and Compute send in <c>error.errors[].reason</c> (their own JSON
/// shape, not <c>google.rpc</c>) are read, so the failed-precondition rule can use them. The body is Google's documented
/// shape, parsed through the same entry the gateways use.
/// </summary>
public class GoogleApiErrorsTests
{
    private static GoogleApiException Failure(int http, string body)
        => new("Test.Service", "ignored") { HttpStatusCode = (HttpStatusCode)http, Error = new RequestError { ErrorResponseContent = body } };

    private static string Body(int http, string message, params string[] reasons)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + System.Text.Json.JsonSerializer.Serialize(message)
            + ",\"errors\":[" + string.Join(",", reasons.Select(r => "{\"reason\":\"" + r + "\",\"domain\":\"global\"}")) + "]}}";

    [Fact]
    public void The_reasons_of_a_Storage_or_Compute_error_body_are_read_from_errors()
    {
        var status = GoogleApiErrors.FromApiException(Failure(412, Body(412, "Supplied fingerprint does not match", "conditionNotMet")));

        status.Reasons.ShouldBe(["conditionNotMet"]);
    }

    [Fact]
    public void A_412_whose_only_signal_is_the_errors_reason_is_a_failed_precondition_not_an_org_policy_refusal()
    {
        var status = GoogleApiErrors.FromApiException(Failure(412, Body(412, "Supplied fingerprint does not match", "conditionNotMet")));

        GoogleApiErrors.KindOf(status).ShouldNotBe(CloudErrorKind.OrgPolicy);
    }

    [Fact]
    public void A_412_conditionNotMet_that_quotes_a_constraint_is_still_an_org_policy_refusal()
    {
        var status = GoogleApiErrors.FromApiException(Failure(412, Body(412, "Blocked by constraints/storage.retentionPolicySeconds", "conditionNotMet")));

        GoogleApiErrors.KindOf(status).ShouldBe(CloudErrorKind.OrgPolicy);
    }

    [Fact]
    public void A_412_with_no_reason_and_neutral_wording_is_an_org_policy_refusal()
    {
        var status = GoogleApiErrors.FromApiException(Failure(412, Body(412, "The request was blocked by an administrator policy.", "forbidden")));

        GoogleApiErrors.KindOf(status).ShouldBe(CloudErrorKind.OrgPolicy);
    }
}
