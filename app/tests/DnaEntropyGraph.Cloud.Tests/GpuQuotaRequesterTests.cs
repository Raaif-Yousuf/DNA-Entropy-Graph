using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #99 (the wizard's GPU step): ask Google for the L4 quota and the project-wide GPU cap, but only after reading whether the project
/// may ask at all, and never when the quota is already there. The decisive observables are the requests the fake recorded and the order
/// they came in: an eligibility read before every create, no create for an ineligible or already-sufficient metric.
/// </summary>
public class GpuQuotaRequesterTests
{
    private const string Project = "my-project";
    private const string Region = "us-central1";

    private static GpuQuotaRequester Requester(FakeGcp gcp) => new(gcp);

    private static FakeGcp NoQuota() => new FakeGcp().WithGpuQuota(Region, "nvidia-l4", 0).WithAllRegionsGpuCap(0);

    private static QuotaRequestItem Item(GpuQuotaRequestReport report, string metric) => report.Items.Single(i => i.Metric == metric);

    [Fact]
    public async Task With_no_quota_and_an_eligible_project_it_asks_for_one_L4_in_the_region_and_one_project_wide_after_reading_eligibility_for_each()
    {
        var gcp = NoQuota();

        var report = await Requester(gcp).RequestAsync(Project, Region, "lab@example.org", CancellationToken.None);

        gcp.QuotaCalls.ShouldBe(
        [
            "eligibility:" + QuotaMetrics.NvidiaL4Gpus,
            "request:" + QuotaMetrics.NvidiaL4Gpus,
            "eligibility:" + QuotaMetrics.GpusAllRegions,
            "request:" + QuotaMetrics.GpusAllRegions,
        ]);
        var l4 = gcp.QuotaRequests.Single(r => r.Metric == QuotaMetrics.NvidiaL4Gpus);
        l4.Region.ShouldBe(Region);
        l4.PreferredValue.ShouldBe(1);
        l4.ContactEmail.ShouldBe("lab@example.org");
        l4.Justification.ShouldNotBeNullOrWhiteSpace();
        var all = gcp.QuotaRequests.Single(r => r.Metric == QuotaMetrics.GpusAllRegions);
        all.Region.ShouldBeNull("the project-wide cap has no region");
        all.PreferredValue.ShouldBe(1);
        Item(report, QuotaMetrics.NvidiaL4Gpus).Outcome.ShouldBe(QuotaRequestOutcome.Requested);
        Item(report, QuotaMetrics.GpusAllRegions).Outcome.ShouldBe(QuotaRequestOutcome.Requested);
        report.Sufficient.ShouldBeFalse("a request is pending review, not a grant");
        report.AnyRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task An_automatically_granted_request_is_reported_granted_and_the_quota_read_then_shows_a_GPU()
    {
        var gcp = NoQuota().WithQuotaAutoGrant();

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        report.Items.ShouldAllBe(i => i.Outcome == QuotaRequestOutcome.Granted);
        report.Sufficient.ShouldBeTrue();
        (await gcp.GetGpuQuotaAsync(Project, Region, "nvidia-l4", CancellationToken.None)).ShouldBe(1, "the grant must really change what the preflight reads");
    }

    [Fact]
    public async Task Quota_that_is_already_there_asks_for_nothing_and_does_not_even_read_eligibility()
    {
        var gcp = new FakeGcp();

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        report.Items.ShouldAllBe(i => i.Outcome == QuotaRequestOutcome.AlreadyAvailable);
        gcp.QuotaCalls.ShouldBeEmpty();
        report.Sufficient.ShouldBeTrue();
        report.AnyRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_the_missing_half_is_requested()
    {
        var gcp = new FakeGcp().WithGpuQuota(Region, "nvidia-l4", 0);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        gcp.QuotaRequests.Select(r => r.Metric).ShouldBe([QuotaMetrics.NvidiaL4Gpus]);
        Item(report, QuotaMetrics.GpusAllRegions).Outcome.ShouldBe(QuotaRequestOutcome.AlreadyAvailable);
    }

    [Theory]
    [InlineData(QuotaIneligibilityReason.NoValidBillingAccount)]
    [InlineData(QuotaIneligibilityReason.NotEnoughUsageHistory)]
    [InlineData(QuotaIneligibilityReason.NotSupported)]
    [InlineData(QuotaIneligibilityReason.Other)]
    public async Task An_ineligible_project_is_told_why_and_no_request_is_created(QuotaIneligibilityReason reason)
    {
        var gcp = NoQuota().WithQuotaEligibility(QuotaMetrics.NvidiaL4Gpus, eligible: false, reason);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        var l4 = Item(report, QuotaMetrics.NvidiaL4Gpus);
        l4.Outcome.ShouldBe(QuotaRequestOutcome.NotEligible);
        l4.Ineligibility.ShouldBe(reason);
        gcp.QuotaRequests.ShouldNotContain(r => r.Metric == QuotaMetrics.NvidiaL4Gpus, "an ineligible metric must never reach the create");
        Item(report, QuotaMetrics.GpusAllRegions).Outcome.ShouldBe(QuotaRequestOutcome.Requested, "the other metric is still asked for");
    }

    [Fact]
    public async Task An_eligibility_that_cannot_be_read_is_a_failure_with_its_kind_and_not_a_blind_create()
    {
        var gcp = NoQuota().WithQuotaEligibilityFailure(CloudErrorKind.ApiDisabled);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        report.Items.ShouldAllBe(i => i.Outcome == QuotaRequestOutcome.Failed && i.ErrorKind == CloudErrorKind.ApiDisabled);
        gcp.QuotaRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_eligibility_answer_that_says_neither_yes_nor_no_is_a_failure_not_permission_to_ask()
    {
        var gcp = NoQuota().WithQuotaEligibility(QuotaMetrics.NvidiaL4Gpus, eligible: null, QuotaIneligibilityReason.None);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        var l4 = Item(report, QuotaMetrics.NvidiaL4Gpus);
        l4.Outcome.ShouldBe(QuotaRequestOutcome.Failed);
        l4.Detail.ShouldBe("QUOTA_ELIGIBILITY_UNKNOWN");
        gcp.QuotaRequests.ShouldNotContain(r => r.Metric == QuotaMetrics.NvidiaL4Gpus);
    }

    [Fact]
    public async Task A_failed_create_is_reported_with_its_kind_and_does_not_stop_the_other_metric()
    {
        var gcp = NoQuota().WithQuotaRequestFailure(QuotaMetrics.NvidiaL4Gpus, CloudErrorKind.Permission);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        var l4 = Item(report, QuotaMetrics.NvidiaL4Gpus);
        l4.Outcome.ShouldBe(QuotaRequestOutcome.Failed);
        l4.ErrorKind.ShouldBe(CloudErrorKind.Permission);
        Item(report, QuotaMetrics.GpusAllRegions).Outcome.ShouldBe(QuotaRequestOutcome.Requested);
    }

    [Fact]
    public async Task Asking_twice_in_one_day_reuses_the_preference_and_reports_already_requested_not_an_error()
    {
        var gcp = NoQuota();
        await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        var again = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        again.Items.ShouldAllBe(i => i.Outcome == QuotaRequestOutcome.AlreadyRequested);
        gcp.QuotaRequests.Select(r => r.PreferenceId).Distinct().Count().ShouldBe(2, "the same two preference ids both times");
    }

    [Fact]
    public async Task The_preference_ids_are_stable_across_days_and_legal_resource_ids()
    {
        var gcp = NoQuota();

        await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        gcp.QuotaRequests.Select(r => r.PreferenceId).ShouldBe(["deg-nvidia-l4-gpus-us-central1", "deg-gpus-all-regions"]);
    }

    [Fact]
    public async Task A_quota_read_that_failed_does_not_count_as_quota_present_and_creates_no_request_so_the_user_retries()
    {
        var gcp = NoQuota().WithQuotaReadFailing(CloudErrorKind.Network);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        report.Items.ShouldAllBe(i => i.Outcome == QuotaRequestOutcome.Failed && i.Detail == GpuQuotaRequester.QuotaReadFailedCode && i.ErrorKind == CloudErrorKind.Network);
        gcp.QuotaRequests.ShouldBeEmpty("a request built on an unknown limit could ask for less than the project has");
        gcp.QuotaCalls.ShouldBeEmpty();
        report.Sufficient.ShouldBeFalse();
    }

    [Fact]
    public async Task A_limit_of_one_fully_used_asks_for_two_not_a_constant_one()
    {
        var gcp = new FakeGcp().WithGpuQuotaUsed(Region, "nvidia-l4", limit: 1, used: 1).WithAllRegionsGpuCap(0);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        gcp.QuotaRequests.Single(r => r.Metric == QuotaMetrics.NvidiaL4Gpus).PreferredValue.ShouldBe(2);
        gcp.QuotaRequests.Single(r => r.Metric == QuotaMetrics.GpusAllRegions).PreferredValue.ShouldBe(1, "a cap of 0 asks for 1");
        Item(report, QuotaMetrics.NvidiaL4Gpus).Outcome.ShouldBe(QuotaRequestOutcome.Requested);
    }

    [Fact]
    public async Task A_limit_of_one_with_nothing_used_asks_for_nothing()
    {
        var gcp = new FakeGcp().WithGpuQuotaUsed(Region, "nvidia-l4", limit: 1, used: 0);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        Item(report, QuotaMetrics.NvidiaL4Gpus).Outcome.ShouldBe(QuotaRequestOutcome.AlreadyAvailable);
        gcp.QuotaRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_preference_another_day_or_the_console_left_under_a_different_id_is_reused_not_conflicted_with()
    {
        var gcp = NoQuota().WithExistingQuotaPreference(QuotaMetrics.NvidiaL4Gpus, Region, "deg-nvidia-l4-gpus-us-central1-20261014", 1);

        var report = await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        Item(report, QuotaMetrics.NvidiaL4Gpus).Outcome.ShouldBe(QuotaRequestOutcome.AlreadyRequested);
        Item(report, QuotaMetrics.GpusAllRegions).Outcome.ShouldBe(QuotaRequestOutcome.Requested);
    }

    [Fact]
    public async Task A_Spot_run_is_covered_by_the_standard_L4_request_and_no_preemptible_quota_is_ever_asked_for()
    {
        // MEASURED 2026-10-15 (compute/resource-usage): Spot consumes standard quota until a preemptible quota is requested.
        QuotaMetrics.QuotaIdFor(QuotaMetrics.ForAccelerator("nvidia-l4", spot: true)!).ShouldBeNull();
        var gcp = NoQuota();

        await Requester(gcp).RequestAsync(Project, Region, null, CancellationToken.None);

        gcp.QuotaRequests.ShouldAllBe(r => !r.Metric.StartsWith("PREEMPTIBLE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("US-CENTRAL1")]
    [InlineData("us-central1\n")]
    [InlineData("a b")]
    public async Task A_region_that_is_not_a_region_name_is_refused_before_any_call(string region)
    {
        var gcp = NoQuota();

        await Should.ThrowAsync<ArgumentException>(() => Requester(gcp).RequestAsync(Project, region, null, CancellationToken.None));

        gcp.QuotaCalls.ShouldBeEmpty();
    }
}
