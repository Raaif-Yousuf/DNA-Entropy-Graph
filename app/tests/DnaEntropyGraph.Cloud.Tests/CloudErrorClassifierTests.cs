using System.Text.Json;
using System.Text.Json.Serialization;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #57. The decisive tests are the fixture-driven ones: every case in
/// <c>tests/contract-fixtures/cloud_error_classification.json</c> (the
/// prototype's own, already-measured behaviour) must classify to its
/// recorded bucket. The structured-signal tests below cover the part the
/// fixture cannot, because the fixture is stderr-only: a real
/// <c>Operation.Error</c>'s structured code/HTTP status, which
/// <see cref="CloudErrorClassifier"/> is required to check first.
/// </summary>
public class CloudErrorClassifierTests
{
    private static readonly Dictionary<string, CloudErrorKind> BucketToKind = new(StringComparer.Ordinal)
    {
        ["billing"] = CloudErrorKind.Billing,
        ["api_disabled"] = CloudErrorKind.ApiDisabled,
        ["quota"] = CloudErrorKind.Quota,
        ["stockout"] = CloudErrorKind.Stockout,
        ["already_exists"] = CloudErrorKind.AlreadyExists,
        ["permission"] = CloudErrorKind.Permission,
        ["network"] = CloudErrorKind.Network,
        ["other"] = CloudErrorKind.Other,
    };

    private static FixtureFile LoadFixture()
    {
        var json = File.ReadAllText(FixturePaths.CloudErrorClassificationJson);
        var fixture = JsonSerializer.Deserialize<FixtureFile>(json);
        fixture.ShouldNotBeNull("the fixture file must parse - a null result here means the JSON shape drifted from what this test expects.");
        return fixture!;
    }

    public static IEnumerable<object?[]> FixtureCases()
    {
        foreach (var c in LoadFixture().Cases)
        {
            yield return new object?[] { c.Id, c.Stderr, c.ExpectedBucket, c.HttpStatus };
        }
    }

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void Every_recorded_fixture_case_classifies_to_its_expected_bucket(string id, string stderr, string expectedBucket, int? httpStatus)
    {
        var expectedKind = BucketToKind[expectedBucket];

        var actual = CloudErrorClassifier.Classify(new CloudError(Code: null, HttpStatus: httpStatus, Message: stderr));

        actual.ShouldBe(expectedKind, $"case '{id}': stderr '{stderr}' should classify as '{expectedBucket}' but got '{actual}'.");
    }

    [Fact]
    public void The_fixture_actually_has_cases_a_vacuous_theory_would_hide()
    {
        LoadFixture().Cases.Length.ShouldBeGreaterThanOrEqualTo(15, "the fixture is checked into the repo with 18 cases; finding fewer means this test stopped reading the real file.");
    }

    [Fact]
    public void The_fixtures_own_recorded_evaluation_order_starts_with_billing_before_quota()
    {
        // Asserts against the fixture's own evaluation_order field, not a
        // copy of it, so a future fixture edit that reorders the buckets
        // fails this test rather than silently going unnoticed.
        var order = LoadFixture().EvaluationOrder;

        Array.IndexOf(order, "billing").ShouldBeLessThan(Array.IndexOf(order, "quota"));
    }

    [Fact]
    public void Billing_is_still_chosen_when_the_message_also_mentions_quota_and_account()
    {
        // The exact ordering hazard the fixture's matching_notes call out:
        // "billing is checked before quota because some billing errors also
        // mention the word 'account'." A classifier that checked quota
        // first would mislabel this as quota and send the user hunting for
        // GPU capacity when their billing account is simply unlinked
        // (CLAUDE.md Critical Pitfalls).
        var message = "Billing account for this project has a quota-adjacent problem: account not active";

        CloudErrorClassifier.Classify(new CloudError(null, null, message)).ShouldBe(CloudErrorKind.Billing);
    }

    [Theory]
    [InlineData("ZONE_RESOURCE_POOL_EXHAUSTED", null, "no message needed for a structured code", CloudErrorKind.Stockout)]
    [InlineData("QUOTA_EXCEEDED", null, "Quota 'NVIDIA_L4_GPUS' exceeded. Limit: 0.0", CloudErrorKind.Quota)]
    [InlineData("BILLING_DISABLED", null, "billing account disabled", CloudErrorKind.Billing)]
    [InlineData(null, 403, "accessNotConfigured: Compute Engine API is disabled", CloudErrorKind.ApiDisabled)]
    [InlineData(null, 403, "The caller does not have permission, forbidden", CloudErrorKind.Permission)]
    [InlineData("CONDITION_NOT_MET", 412, "Operation denied by constraints/compute.requireOsLogin", CloudErrorKind.OrgPolicy)]
    [InlineData(null, 409, "already exists", CloudErrorKind.AlreadyExists)]
    public void Structured_signals_are_classified_correctly(string? code, int? httpStatus, string message, CloudErrorKind expected)
    {
        CloudErrorClassifier.Classify(new CloudError(code, httpStatus, message)).ShouldBe(expected);
    }

    // Issue #772. THEORY (unverified, no live project): the documented text Compute answers an insert with when the zone does not offer the machine type.
    [Theory]
    [InlineData("NOT_FOUND", 404, "The resource 'projects/my-project/zones/us-central1-c/machineTypes/a2-ultragpu-1g' was not found")]
    [InlineData("notFound", 404, "The resource 'projects/my-iam-project/zones/us-east4-c/machineTypes/a3-highgpu-1g' was not found")]
    [InlineData("notFound", 404, "The resource 'projects/williams-lab/zones/us-east4-c/machineTypes/a3-highgpu-1g' was not found")]
    [InlineData(null, 404, "The resource 'projects/p/zones/z/machineTypes/g2-standard-8' was not found")]
    public void A_machine_type_the_zone_does_not_offer_is_a_per_zone_stockout_so_the_ladder_moves_on(string? code, int? httpStatus, string message)
    {
        CloudErrorClassifier.Classify(new CloudError(code, httpStatus, message)).ShouldBe(CloudErrorKind.Stockout);
    }

    [Theory]
    [InlineData("NOT_FOUND", 404, "The resource 'projects/my-project/zones/us-central1-a/instances/deg-job-1' was not found")]
    [InlineData("NOT_FOUND", 404, "The resource 'projects/my-project/zones/us-central1-a/disks/deg-job-1' was not found")]
    [InlineData("NOT_FOUND", 404, "The resource 'projects/my-project/global/images/family-x' was not found")]
    public void Any_other_missing_resource_is_not_a_stockout_and_stays_other(string code, int httpStatus, string message)
    {
        CloudErrorClassifier.Classify(new CloudError(code, httpStatus, message)).ShouldBe(CloudErrorKind.Other);
    }

    [Fact]
    public void A_machine_type_not_found_without_an_http_status_is_not_decided_by_the_machine_type_rule()
    {
        // A status-less message is the substring stage's to judge (the only route to billing, API-off, permission and quota for it); the machine-type rule is for a real 404 only.
        CloudErrorClassifier.Classify(new CloudError(null, null, "The resource 'projects/my-project/zones/us-central1-c/machineTypes/a2-ultragpu-1g' was not found"))
            .ShouldBe(CloudErrorKind.Other);
    }

    [Theory]
    [InlineData(404, "Billing account for project p is disabled: the resource 'projects/p/zones/z/machineTypes/g2-standard-8' was not found", CloudErrorKind.Billing)]
    [InlineData(null, "Billing account for project p is disabled: the resource 'projects/p/zones/z/machineTypes/g2-standard-8' was not found", CloudErrorKind.Billing)]
    [InlineData(404, "Compute Engine API has not been used in project p before or it is disabled. The resource 'projects/p/zones/z/machineTypes/g2-standard-8' was not found", CloudErrorKind.ApiDisabled)]
    [InlineData(null, "Compute Engine API has not been used in project p before or it is disabled. The resource 'projects/p/zones/z/machineTypes/g2-standard-8' was not found", CloudErrorKind.ApiDisabled)]
    [InlineData(404, "Quota 'NVIDIA_L4_GPUS' exceeded. The resource 'projects/p/zones/z/machineTypes/g2-standard-8' was not found", CloudErrorKind.Quota)]
    [InlineData(null, "Quota 'NVIDIA_L4_GPUS' exceeded. The resource 'projects/p/zones/z/machineTypes/g2-standard-8' was not found", CloudErrorKind.Quota)]
    public void A_machine_type_not_found_never_hides_a_project_wide_refusal_in_the_same_message(int? httpStatus, string message, CloudErrorKind expected)
    {
        CloudErrorClassifier.Classify(new CloudError(null, httpStatus, message)).ShouldBe(expected);
    }

    [Fact]
    public void A_403_that_names_a_machine_type_is_still_a_permission_error()
    {
        // The 403 rules run first: a refusal that happens to name a machine type is still a permission error.
        CloudErrorClassifier.Classify(new CloudError("IAM_PERMISSION_DENIED", 403, "Permission 'compute.machineTypes.get' denied on machineTypes/g2-standard-8; resource was not found"))
            .ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public void The_772_fixture_entries_carry_a_404_and_say_they_are_not_recorded_payloads()
    {
        var cases = LoadFixture().Cases.Where(c => c.Id.EndsWith("-772", StringComparison.Ordinal)).ToList();

        cases.Count.ShouldBe(3);
        cases.ShouldAllBe(c => c.HttpStatus == 404 && c.Recorded == false);
    }
    // Issue #539: a permission denial that names a billing permission is a missing role, not "billing is off".
    [Theory]
    [InlineData("IAM_PERMISSION_DENIED", 403, "Permission 'billing.resourceAssociations.create' denied on resource '//cloudbilling.googleapis.com/billingAccounts/AAA'.")]
    [InlineData("PERMISSION_DENIED", 403, "The caller does not have permission billing.resourceAssociations.get")]
    [InlineData("PERMISSION_DENIED", 403, "Permission 'billing.accounts.list' denied on the billing account")]
    [InlineData(null, 403, "Permission 'billing.resourceAssociations.create' denied")]
    [InlineData(null, null, "Permission 'billing.resourceAssociations.create' denied on billing account AAA")]
    public void A_permission_denial_that_mentions_billing_is_permission_and_keeps_the_permission_name(string? code, int? httpStatus, string message)
    {
        var error = new CloudError(code, httpStatus, message);

        CloudErrorClassifier.Classify(error).ShouldBe(CloudErrorKind.Permission);
        error.Message.ShouldContain("billing.");
    }

    [Theory]
    [InlineData("BILLING_DISABLED", 403, "This API method requires billing to be enabled.")]
    [InlineData(null, 403, "Billing must be enabled for activation of service(s)")]
    [InlineData(null, 403, "The billing account for the owning project is disabled in state absent")]
    [InlineData(null, 403, "Billing account is not active for this project")]
    public void A_403_that_says_billing_is_off_stays_billing(string? code, int? httpStatus, string message)
    {
        CloudErrorClassifier.Classify(new CloudError(code, httpStatus, message)).ShouldBe(CloudErrorKind.Billing);
    }

    [Fact]
    public void A_structured_QUOTA_EXCEEDED_code_is_never_confused_with_a_structured_stockout_code()
    {
        // CLAUDE.md Critical Pitfalls: "Quota is not stockout." Both errors
        // can otherwise read as "GCP said no" - the structured code is the
        // only thing telling them apart, and this test proves the
        // classifier actually uses it rather than falling through to a
        // shared "capacity problem" bucket.
        var quota = CloudErrorClassifier.Classify(new CloudError("QUOTA_EXCEEDED", null, "does not have enough resources in region"));
        var stockout = CloudErrorClassifier.Classify(new CloudError("ZONE_RESOURCE_POOL_EXHAUSTED", null, "quota-ish wording that would otherwise mislead"));

        quota.ShouldBe(CloudErrorKind.Quota);
        stockout.ShouldBe(CloudErrorKind.Stockout);
    }

    // Issue #539 (round 3): the permission-denial rule is for a 403 (or an error with no status). A 412 or a 409 that
    // happens to say "does not have permission" keeps its own structured meaning.
    [Theory]
    [InlineData(412, "The request does not have permission to proceed: blocked by constraints/gcp.restrictServiceUsage", CloudErrorKind.OrgPolicy)]
    [InlineData(409, "Resource already exists; the caller does not have permission to overwrite it", CloudErrorKind.AlreadyExists)]
    [InlineData(403, "The caller does not have permission", CloudErrorKind.Permission)]
    [InlineData(null, "The caller does not have permission", CloudErrorKind.Permission)]
    public void A_permission_denial_wording_only_decides_a_403_or_an_unknown_status(int? status, string message, CloudErrorKind expected)
    {
        CloudErrorClassifier.Classify(new CloudError(null, status, message)).ShouldBe(expected);
    }

    // Issue #54 (rounds 2 and 3): THEORY (unverified): Cloud Storage answers a failed precondition (an etag or generation
    // that did not match) with 412. A 412 is a precondition conflict ONLY when it positively looks like one (the reason
    // "conditionNotMet", or "Precondition Failed" wording); every other 412, with or without a "constraints/" id, stays an
    // org-policy refusal as before. ToTest has the row that captures the real shapes.
    [Theory]
    [InlineData(null, "At least one of the pre-conditions you specified did not hold.", CloudErrorKind.Other)]
    [InlineData("conditionNotMet", "Precondition Failed", CloudErrorKind.Other)]
    [InlineData(null, "Request violates constraints/storage.retentionPolicySeconds.", CloudErrorKind.OrgPolicy)]
    [InlineData("FAILED_PRECONDITION", "Operation blocked by an administrator policy on this project.", CloudErrorKind.OrgPolicy)]
    [InlineData(null, "The request was refused.", CloudErrorKind.OrgPolicy)]
    [InlineData("FAILED_PRECONDITION", "Precondition check failed: constraints/compute.requireOsLogin is enforced.", CloudErrorKind.OrgPolicy)]
    [InlineData("CONDITION_NOT_MET", "Operation denied", CloudErrorKind.OrgPolicy)]
    [InlineData("CONDITION_NOT_MET", "Precondition Failed", CloudErrorKind.Other)]
    public void A_412_is_a_precondition_conflict_only_when_it_positively_looks_like_one(string? code, string message, CloudErrorKind expected)
    {
        CloudErrorClassifier.Classify(new CloudError(code, 412, message)).ShouldBe(expected);
    }

    // Where there is no HTTP 412 to decide by, the structured CONDITION_NOT_MET code is the only signal and means org policy.
    [Theory]
    [InlineData(null, "Operation denied")]
    [InlineData(503, "Operation denied")]
    public void The_CONDITION_NOT_MET_code_is_an_org_policy_refusal_when_there_is_no_412_to_decide_by(int? status, string message)
    {
        CloudErrorClassifier.Classify(new CloudError("CONDITION_NOT_MET", status, message)).ShouldBe(CloudErrorKind.OrgPolicy);
    }
    // Round 4: an org-policy marker (a constraints/ id) beats the permission wording when no status says otherwise,
    // and "billing is required" on a 403 is billing off, not Other.
    [Theory]
    [InlineData(null, "Constraint constraints/compute.vmExternalIpAccess violated; the caller does not have permission", CloudErrorKind.OrgPolicy)]
    [InlineData(null, "Permission 'compute.instances.create' denied by constraints/compute.requireOsLogin", CloudErrorKind.OrgPolicy)]
    [InlineData(403, "Billing is required for this project", CloudErrorKind.Billing)]
    [InlineData(403, "This API method requires billing: billing is required", CloudErrorKind.Billing)]
    [InlineData(403, "The caller does not have permission; billing is required on the billing account", CloudErrorKind.Permission)]
    [InlineData(403, "Caller does not have required permission to use project 123. Use another project to pass your quota and billing.", CloudErrorKind.Permission)]
    public void An_org_policy_marker_wins_without_a_status_and_billing_required_is_billing(int? status, string message, CloudErrorKind expected)
    {
        CloudErrorClassifier.Classify(new CloudError(null, status, message)).ShouldBe(expected);
    }

    [Fact]
    public void Org_policy_has_no_substring_fallback_unlike_the_other_eight_buckets()
    {
        // docs/cloud_design.md section 5 and issue #57's own comment: the
        // prototype never had an org_policy bucket, so - unlike the other
        // eight - it is reachable only through the structured HTTP 412 /
        // CONDITION_NOT_MET signal, never through message text alone.
        var messageOnly = CloudErrorClassifier.Classify(new CloudError(null, null, "blocked by an org policy constraint on this project"));

        messageOnly.ShouldNotBe(CloudErrorKind.OrgPolicy);
    }

    // Issue #54: a user who may create VMs but may not run one AS the worker service account gets a 403 naming the
    // actAs permission or the Service Account User role. It stays the "permission" bucket (an abort) but needs its own code
    // and action (copy a request for the owner), not the generic "not allowed to create computers".
    [Theory]
    [InlineData(403, "Required 'iam.serviceAccounts.actAs' permission for 'projects/my-lab/serviceAccounts/dna-entropy-worker@my-lab.iam.gserviceaccount.com'")]
    [InlineData(403, "The user does not have access to service account 'dna-entropy-worker@my-lab.iam.gserviceaccount.com'.  User: 'a@b.org'.  Ask a project owner to grant you the iam.serviceAccountUser role on the service account")]
    [InlineData(403, "Permission 'iam.serviceaccounts.actAs' denied on service account x@my-lab.iam.gserviceaccount.com (or it may not exist).")]
    public void An_actAs_refusal_is_the_permission_bucket_and_is_recognised_as_PERMISSION_ACTAS(int status, string message)
    {
        var error = new CloudError("PERMISSION_DENIED", status, message);

        CloudErrorClassifier.Classify(error).ShouldBe(CloudErrorKind.Permission);
        CloudErrorClassifier.IsActAsDenial(error).ShouldBeTrue();
    }

    [Theory]
    [InlineData(403, "Required 'compute.instances.create' permission for 'projects/my-lab/zones/us-central1-a/instances/deg-x'")]
    [InlineData(403, "The caller does not have permission")]
    [InlineData(429, "Quota exceeded for quota metric iam.serviceAccounts.actAs")]
    [InlineData(null, "")]
    public void Other_refusals_are_not_PERMISSION_ACTAS(int? status, string message)
    {
        CloudErrorClassifier.IsActAsDenial(new CloudError("PERMISSION_DENIED", status, message)).ShouldBeFalse();
    }

    private sealed record FixtureCase(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("expected_bucket")] string ExpectedBucket,
        [property: JsonPropertyName("http_status")] int? HttpStatus = null,
        [property: JsonPropertyName("recorded")] bool? Recorded = null);

    private sealed record FixtureFile(
        [property: JsonPropertyName("cases")] FixtureCase[] Cases,
        [property: JsonPropertyName("evaluation_order")] string[] EvaluationOrder);

    // ---- Issue #92 (cold review L7): the plausible shapes of an expired flex-start request. THEORY (unverified): none of these is measured; ToTest #92 records the real one. ----

    [Theory]
    [InlineData("FLEX_START_REQUEST_EXPIRED", "The request waited for capacity and was not granted in time.")]
    [InlineData("FLEX_START_CAPACITY_UNAVAILABLE", "")]
    [InlineData("DEADLINE_EXCEEDED", "The request could not be fulfilled: no capacity was found within requestValidForDuration.")]
    [InlineData("OPERATION_ERROR", "Timed out waiting for capacity for the flex-start request.")]
    [InlineData("RESOURCE_AVAILABILITY", "Capacity was not available before the request expired.")]
    public void An_expired_flex_start_request_is_a_stockout(string code, string message)
        => CloudErrorClassifier.Classify(new CloudError(code, null, message)).ShouldBe(CloudErrorKind.Stockout);

    [Theory]
    [InlineData("ACCESS_TOKEN_EXPIRED", "The access token expired.")]
    [InlineData("OPERATION_ERROR", "The request expired.")]
    [InlineData("QUOTA_EXCEEDED", "Quota exceeded: no capacity left in your quota.")]
    public void A_message_that_only_looks_like_an_expired_flex_request_is_not_a_stockout(string code, string message)
        => CloudErrorClassifier.Classify(new CloudError(code, null, message)).ShouldNotBe(CloudErrorKind.Stockout);

    // ---- Issue #92 round 2 ----

    [Fact]
    public void An_invalid_value_for_the_wait_field_is_Other_not_a_stockout_so_the_copy_says_to_save_diagnostics()
    {
        var error = new CloudError("INVALID_FIELD_VALUE", 400, "Invalid value for field 'params.requestValidForDuration': '10800s'. Duration is out of range.");

        CloudErrorClassifier.Classify(error).ShouldBe(CloudErrorKind.Other);
    }

    [Theory]
    [InlineData("FLEX_START_QUOTA_EXCEEDED", null, "Quota 'GPUS_ALL_REGIONS' exceeded. Limit: 0.0.", CloudErrorKind.Quota)]
    [InlineData("FLEX_START_PERMISSION_DENIED", 403, "Required 'compute.instances.create' permission for 'projects/p'", CloudErrorKind.Permission)]
    [InlineData("FLEX_START_FORBIDDEN", 403, "Forbidden", CloudErrorKind.Permission)]
    public void A_flex_code_does_not_override_the_quota_and_permission_rules(string code, int? status, string message, CloudErrorKind expected)
        => CloudErrorClassifier.Classify(new CloudError(code, status, message)).ShouldBe(expected);

    [Fact]
    public void A_flex_code_with_nothing_about_capacity_is_not_a_stockout()
        => CloudErrorClassifier.Classify(new CloudError("FLEX_START_INVALID_ARGUMENT", 400, "Bad field.")).ShouldNotBe(CloudErrorKind.Stockout);

    [Theory]
    [InlineData("Connection timed out while waiting for capacity.")]
    [InlineData("Could not reach the server; capacity request timed out")]
    public void Connection_phrasing_wins_over_a_capacity_timeout(string message)
        => CloudErrorClassifier.Classify(new CloudError("OPERATION_ERROR", null, message)).ShouldBe(CloudErrorKind.Network);

    // ---- Issue #92 round 3 (F1, F4, F5) ----

    [Fact]
    public void F1_a_flex_quota_exhausted_with_a_zero_limit_is_Quota_not_a_stockout()
        => CloudErrorClassifier.Classify(new CloudError("FLEX_START_QUOTA_EXCEEDED", null, "Quota 'GPUS_ALL_REGIONS' exhausted. Limit: 0.0.")).ShouldBe(CloudErrorKind.Quota);

    [Fact]
    public void F1_a_quota_message_that_also_says_capacity_is_still_Quota_for_a_flex_code()
        => CloudErrorClassifier.Classify(new CloudError("FLEX_START_QUOTA_EXCEEDED", null, "Quota GPUS_ALL_REGIONS exceeded: no capacity in your quota.")).ShouldBe(CloudErrorKind.Quota);

    [Fact]
    public void F1_exhausted_alone_is_not_capacity_wording_for_a_flex_code()
        => CloudErrorClassifier.Classify(new CloudError("FLEX_START_ERROR", null, "Some resource is exhausted.")).ShouldNotBe(CloudErrorKind.Stockout);

    [Theory]
    [InlineData("FLEX_START_ERROR", "Connection reset: capacity request expired")]
    [InlineData("FLEX_START_ERROR", "Could not reach the server; the request has expired")]
    [InlineData("FLEX_START_ERROR", "Network is unreachable, capacity not granted")]
    public void F4_connection_phrasing_wins_on_the_flex_path_too(string code, string message)
        => CloudErrorClassifier.Classify(new CloudError(code, null, message)).ShouldBe(CloudErrorKind.Network);

    [Fact]
    public void F4_plain_unavailable_is_not_capacity_for_a_flex_code()
        => CloudErrorClassifier.Classify(new CloudError("FLEX_START_ERROR", null, "Service unavailable.")).ShouldNotBe(CloudErrorKind.Stockout);

    [Theory]
    [InlineData("FLEX_START_ERROR", "The request could not be fulfilled.")]
    [InlineData("FLEX_START_ERROR", "Capacity request was not fulfilled.")]
    [InlineData("FLEX_START_ERROR", "The wait given by requestValidForDuration elapsed.")]
    [InlineData("FLEX_START_ERROR", "Resource unavailable in this zone.")]
    public void F5_flex_expiry_and_unfulfilled_shapes_are_a_stockout(string code, string message)
        => CloudErrorClassifier.Classify(new CloudError(code, null, message)).ShouldBe(CloudErrorKind.Stockout);

    [Fact]
    public void F5_a_400_that_names_the_wait_field_is_never_a_stockout()
    {
        CloudErrorClassifier.Classify(new CloudError("FLEX_START_INVALID", 400, "requestValidForDuration elapsed value is out of range.")).ShouldNotBe(CloudErrorKind.Stockout);
        CloudErrorClassifier.Classify(new CloudError("INVALID_FIELD_VALUE", 400, "requestValidForDuration expired")).ShouldNotBe(CloudErrorKind.Stockout);
    }
}
