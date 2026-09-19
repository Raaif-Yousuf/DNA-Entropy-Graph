using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #49: the scripted failures are the whole value of <see cref="FakeGcp"/>.
/// Every test here scripts exactly one failure shape with a fluent
/// <c>With*</c> call and asserts the resulting <see cref="CloudOperationException"/>
/// is the exact shape a real gateway would throw - and, where useful, that
/// running it back through issue #57's <see cref="CloudErrorClassifier"/>
/// agrees with the kind the fake scripted, proving the two pieces are not
/// two disconnected implementations of the same taxonomy.
/// </summary>
public class FakeGcpScriptedFailureTests
{
    private const string Zone = "us-central1-a";

    private static VmSpec ValidSpec(string jobId = "20260919-084512-ab23cd", string projectId = "my-project") => new(
        ProjectId: projectId,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static async Task<CloudOperationException> CaptureAsync(Func<Task> action)
    {
        var ex = await Should.ThrowAsync<CloudOperationException>(action);
        return ex;
    }

    [Fact]
    public async Task Billing_off_makes_create_throw_billing_and_preflight_report_it_too()
    {
        var gcp = new FakeGcp().WithBillingOff("my-project");

        var ex = await CaptureAsync(() => gcp.CreateVmAsync(ValidSpec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Billing);
        CloudErrorClassifier.Classify(ex.Error).ShouldBe(CloudErrorKind.Billing);
        (await gcp.IsBillingEnabledAsync("my-project", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Compute_api_off_makes_create_throw_api_disabled_and_enabling_it_clears_the_script()
    {
        var gcp = new FakeGcp().WithComputeApiOff("my-project");

        var first = await CaptureAsync(() => gcp.CreateVmAsync(ValidSpec("20260919-084512-aaaaaa"), Zone, CancellationToken.None));
        first.Kind.ShouldBe(CloudErrorKind.ApiDisabled);
        CloudErrorClassifier.Classify(first.Error).ShouldBe(CloudErrorKind.ApiDisabled);

        await gcp.EnableComputeApiAsync("my-project", CancellationToken.None);

        var created = await gcp.CreateVmAsync(ValidSpec("20260919-084512-bbbbbb"), Zone, CancellationToken.None);
        created.Status.ShouldBe("RUNNING");
    }

    [Fact]
    public async Task Permission_denied_makes_create_throw_permission_not_billing_or_api_disabled()
    {
        var gcp = new FakeGcp().WithPermissionDenied("my-project");

        var ex = await CaptureAsync(() => gcp.CreateVmAsync(ValidSpec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        CloudErrorClassifier.Classify(ex.Error).ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task Org_policy_blocked_makes_create_throw_org_policy()
    {
        var gcp = new FakeGcp().WithOrgPolicyBlocked("my-project");

        var ex = await CaptureAsync(() => gcp.CreateVmAsync(ValidSpec(), Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
        CloudErrorClassifier.Classify(ex.Error).ShouldBe(CloudErrorKind.OrgPolicy);
    }

    [Fact]
    public async Task The_first_two_zones_stock_out_and_the_third_succeeds()
    {
        // The exact scenario the lane brief names as the bar for this
        // issue: one line per zone to script, and the ladder's own retry
        // loop (simulated here as three sequential CreateVmAsync calls with
        // the same spec/name in different zones - docs/cloud_design.md
        // section 3) sees the third succeed.
        var gcp = new FakeGcp()
            .WithZoneStockout("us-central1-a")
            .WithZoneStockout("us-central1-b");

        var spec = ValidSpec();

        var first = await CaptureAsync(() => gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None));
        var second = await CaptureAsync(() => gcp.CreateVmAsync(spec, "us-central1-b", CancellationToken.None));
        var third = await gcp.CreateVmAsync(spec, "us-central1-c", CancellationToken.None);

        first.Kind.ShouldBe(CloudErrorKind.Stockout);
        second.Kind.ShouldBe(CloudErrorKind.Stockout);
        third.Status.ShouldBe("RUNNING");
        third.Zone.ShouldBe("us-central1-c");
    }

    [Fact]
    public async Task A_zone_scripted_to_stock_out_a_fixed_number_of_times_then_succeeds_on_retry()
    {
        var gcp = new FakeGcp().WithZoneStockout("us-central1-a", times: 2);
        var spec = ValidSpec();

        await CaptureAsync(() => gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None));
        await CaptureAsync(() => gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None));
        var third = await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);

        third.Status.ShouldBe("RUNNING");
    }

    [Fact]
    public async Task Quota_exceeded_on_create_is_a_distinct_kind_from_stockout_even_though_both_are_GCP_saying_no()
    {
        // CLAUDE.md Critical Pitfalls: "Quota is not stockout." Scripted
        // separately here so a test can prove a caller distinguishes them,
        // not just that both fail.
        var gcp = new FakeGcp()
            .WithQuotaExceededOnCreate("us-central1-a")
            .WithZoneStockout("us-central1-b");

        var spec = ValidSpec();

        var quota = await CaptureAsync(() => gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None));
        var stockout = await CaptureAsync(() => gcp.CreateVmAsync(spec, "us-central1-b", CancellationToken.None));

        quota.Kind.ShouldBe(CloudErrorKind.Quota);
        stockout.Kind.ShouldBe(CloudErrorKind.Stockout);
    }

    [Fact]
    public async Task Already_exists_is_thrown_every_time_unlike_the_transient_zone_failures_and_the_vm_can_be_adopted()
    {
        var gcp = new FakeGcp().WithAlreadyExists("deg-20260919-084512-ab23cd", Zone);
        var spec = ValidSpec();

        var firstAttempt = await CaptureAsync(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));
        var secondAttempt = await CaptureAsync(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));

        firstAttempt.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
        secondAttempt.Kind.ShouldBe(CloudErrorKind.AlreadyExists);

        var adopted = await gcp.GetVmAsync(spec.VmName, Zone, CancellationToken.None);
        adopted.ShouldNotBeNull();
        adopted!.Status.ShouldBe("RUNNING");
    }

    [Fact]
    public async Task Network_failures_are_transient_and_clear_after_the_scripted_count()
    {
        var gcp = new FakeGcp().WithNetworkFailures(2);
        var spec = ValidSpec();

        var first = await CaptureAsync(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));
        var second = await CaptureAsync(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));
        var third = await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);

        first.Kind.ShouldBe(CloudErrorKind.Network);
        second.Kind.ShouldBe(CloudErrorKind.Network);
        third.Status.ShouldBe("RUNNING");
    }

    [Fact]
    public async Task GpuQuota_all_regions_cap_of_zero_overrides_a_positive_regional_quota()
    {
        // CLAUDE.md Critical Pitfalls: "GPUS_ALL_REGIONS=0 overrides a
        // regional 1." A project can show plenty of regional L4 quota and
        // still be unable to create a single GPU VM anywhere.
        var gcp = new FakeGcp()
            .WithGpuQuota("us-central1", "nvidia-l4", available: 4)
            .WithAllRegionsGpuCap(0);

        var quota = await gcp.GetGpuQuotaAsync("my-project", "us-central1", "nvidia-l4", CancellationToken.None);

        quota.ShouldBe(0);
    }

    [Fact]
    public async Task GpuQuota_regional_value_is_used_when_no_all_regions_cap_is_scripted()
    {
        var gcp = new FakeGcp().WithGpuQuota("us-central1", "nvidia-l4", available: 6);

        var quota = await gcp.GetGpuQuotaAsync("my-project", "us-central1", "nvidia-l4", CancellationToken.None);

        quota.ShouldBe(6);
    }

    [Fact]
    public async Task A_running_vm_is_discovered_terminated_and_marked_preempted_once_the_scripted_deadline_passes()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-19T08:00:00Z"));
        var gcp = new FakeGcp(time).WithPreemption("deg-20260919-084512-ab23cd", Zone, TimeSpan.FromMinutes(10));
        var spec = ValidSpec();

        var created = await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        var beforeDeadline = await gcp.GetVmAsync(created.Name, Zone, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(11));
        var afterDeadline = await gcp.GetVmAsync(created.Name, Zone, CancellationToken.None);

        beforeDeadline!.Status.ShouldBe("RUNNING");
        afterDeadline!.Status.ShouldBe("TERMINATED");
        afterDeadline.StatusReason.ShouldBe("preempted");
    }

    [Fact]
    public async Task A_scripted_project_wide_failure_still_only_fires_after_EnsurePreconditions_passes()
    {
        // Ordering proof for #49: even with a project-wide failure
        // scripted, a spec missing a label throws the label error, not the
        // scripted billing error - EnsurePreconditions runs unconditionally
        // first, exactly as a real gateway must.
        var gcp = new FakeGcp().WithBillingOff("my-project");
        var brokenSpec = ValidSpec() with { Model = "" };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => gcp.CreateVmAsync(brokenSpec, Zone, CancellationToken.None));

        ex.ShouldNotBeOfType<CloudOperationException>();
        ex.Message.ShouldContain("model");
    }
}

/// <summary>A <see cref="TimeProvider"/> a test can move forward by hand, for deterministic preemption scripting.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset start) => _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;
}
