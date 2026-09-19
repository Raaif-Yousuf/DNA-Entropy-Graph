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

    public static IEnumerable<object[]> FixtureCases()
    {
        foreach (var c in LoadFixture().Cases)
        {
            yield return new object[] { c.Id, c.Stderr, c.ExpectedBucket };
        }
    }

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void Every_recorded_fixture_case_classifies_to_its_expected_bucket(string id, string stderr, string expectedBucket)
    {
        var expectedKind = BucketToKind[expectedBucket];

        var actual = CloudErrorClassifier.Classify(new CloudError(Code: null, HttpStatus: null, Message: stderr));

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

    private sealed record FixtureCase(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("expected_bucket")] string ExpectedBucket);

    private sealed record FixtureFile(
        [property: JsonPropertyName("cases")] FixtureCase[] Cases,
        [property: JsonPropertyName("evaluation_order")] string[] EvaluationOrder);
}
