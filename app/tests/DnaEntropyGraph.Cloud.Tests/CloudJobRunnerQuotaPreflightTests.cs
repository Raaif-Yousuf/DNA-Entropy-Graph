using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #88, docs/cloud_design.md section 6.2: the preflight's policy for a quota that cannot be read. A project-wide failure
/// (permission, billing, api_disabled, org_policy) ends the run with that classified error before any VM exists; any other failure is
/// UNKNOWN and provisioning goes on, because quota is pre-filterable and not a stop sign. Run over <see cref="FakeGcp"/>, whose
/// <c>GetGpuQuotaAsync</c> throws the scripted <see cref="FakeGcp.WithQuotaReadFailing"/> error the way the real gateway does.
/// </summary>
public class CloudJobRunnerQuotaPreflightTests
{
    private static VmSpec Spec(string jobId) => new(
        ProjectId: "my-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    public static TheoryData<CloudErrorKind> ProjectWideKinds => new()
    {
        CloudErrorKind.Permission,
        CloudErrorKind.Billing,
        CloudErrorKind.ApiDisabled,
        CloudErrorKind.OrgPolicy,
    };

    public static TheoryData<CloudErrorKind> UnknownKinds => new()
    {
        CloudErrorKind.Network,
        CloudErrorKind.Other,
    };

    [Fact]
    public void The_theory_data_is_not_vacuous_and_covers_exactly_the_project_wide_kinds()
    {
        ProjectWideKinds.Select(r => r.Data).ShouldAllBe(k => VmFacts.IsProjectWide(k));
        UnknownKinds.Select(r => r.Data).ShouldAllBe(k => !VmFacts.IsProjectWide(k));
        ProjectWideKinds.Count().ShouldBe(4);
    }

    [Theory]
    [MemberData(nameof(ProjectWideKinds))]
    public async Task A_quota_read_that_fails_project_wide_ends_the_run_with_that_classified_error_and_creates_nothing(CloudErrorKind kind)
    {
        var gcp = new FakeGcp().WithQuotaReadFailing(kind);
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, new InMemoryRunRepository());

        var result = await runner.RunAsync(TestInputs.Request("job-88a", Spec("job-88a")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(kind);
        result.FailureCode.ShouldBe(RunErrorCodes.For(kind));
        (await gcp.FindByJobIdAsync("job-88a", CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnknownKinds))]
    public async Task A_quota_read_that_fails_any_other_way_is_unknown_and_the_run_goes_on_to_create_the_vm(CloudErrorKind kind)
    {
        var gcp = new FakeGcp().WithQuotaReadFailing(kind);
        var vmSeenAtRunning = false;
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, new InMemoryRunRepository(), (jobId, phase) =>
        {
            vmSeenAtRunning |= phase == JobPhase.Running && gcp.FindByJobIdAsync(jobId, CancellationToken.None).GetAwaiter().GetResult().Count == 1;
        });

        var result = await runner.RunAsync(TestInputs.Request("job-88b", Spec("job-88b")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        vmSeenAtRunning.ShouldBeTrue("the run never created a VM after the unknown quota");
    }

    // ---- the quota read has its own budget, not the one-request CallTimeout (virtual clock) ----

    private static readonly TimeSpan SlowRead = TimeSpan.FromSeconds(90);

    /// <summary>A quota gateway whose read takes <see cref="SlowRead"/> on the runner's clock.</summary>
    private sealed class SlowQuota(FakeGcp inner, VirtualTimeProvider clock) : IQuotaGateway
    {
        public int Completed { get; private set; }

        public async Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken)
        {
            await Task.Yield();
            clock.Advance(SlowRead);
            cancellationToken.ThrowIfCancellationRequested();
            var answer = await inner.GetGpuQuotaAsync(projectId, region, acceleratorType, cancellationToken);
            Completed++;
            return answer;
        }

        public Task<QuotaReadResult> ReadQuotaAsync(string projectId, CancellationToken cancellationToken) => inner.ReadQuotaAsync(projectId, cancellationToken);
    }

    private static (CloudJobRunner Runner, SlowQuota Quota) NewSlowRunner(TimeSpan quotaBudget)
    {
        var gcp = new FakeGcp();
        var clock = new VirtualTimeProvider();
        var quota = new SlowQuota(gcp, clock);
        var runner = new CloudJobRunner(gcp, gcp, gcp, quota, gcp, new InMemoryRunRepository())
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(5),
            CallTimeout = TimeSpan.FromSeconds(60),
            QuotaReadTimeout = quotaBudget,
            LifecycleTimeout = TimeSpan.FromSeconds(5),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            TimeProvider = clock,
            PollDelay = (span, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(span);
                return Task.CompletedTask;
            },
        };
        return (runner, quota);
    }

    [Fact]
    public async Task A_quota_read_longer_than_the_call_timeout_but_inside_the_quota_budget_completes()
    {
        var (runner, quota) = NewSlowRunner(TimeSpan.FromMinutes(5));

        var result = await runner.RunAsync(TestInputs.Request("job-88c", Spec("job-88c")), CancellationToken.None);

        quota.Completed.ShouldBe(1, "the 90 s quota read was cut by the 60 s one-request deadline");
        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public async Task A_quota_read_past_the_quota_budget_is_cut_and_read_as_unknown_so_the_run_still_goes_on()
    {
        var (runner, quota) = NewSlowRunner(TimeSpan.FromSeconds(80));

        var result = await runner.RunAsync(TestInputs.Request("job-88d", Spec("job-88d")), CancellationToken.None);

        quota.Completed.ShouldBe(0, "the read ran to the end although it was past its budget");
        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public void The_default_quota_budget_is_larger_than_the_one_request_call_timeout()
    {
        var settings = new CloudRunSettings();

        settings.QuotaReadTimeout.ShouldBeGreaterThan(settings.CallTimeout);
    }

    [Fact]
    public void The_runner_forwards_the_quota_budget_to_the_settings_the_preflight_reads()
    {
        var gcp = new FakeGcp();

        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, new InMemoryRunRepository()) { QuotaReadTimeout = TimeSpan.FromSeconds(7) };

        runner.QuotaReadTimeout.ShouldBe(TimeSpan.FromSeconds(7));
    }
}
