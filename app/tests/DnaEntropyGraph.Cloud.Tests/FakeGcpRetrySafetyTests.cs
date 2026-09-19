using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #257: proves the two retry-safety mechanisms the issue names -
/// requestId-equivalent idempotent replay, and ALREADY_EXISTS handling -
/// and, separately, proves issue #389's THEORY about what neither of those
/// two mechanisms covers: a name is unique only within a zone, so a second
/// zone's create is not deduplicated by anything at the <see cref="FakeGcp"/>
/// level. <see cref="CloudJobRunnerTests"/>'s crash-and-resume tests show
/// the actual fix (reconcile via <see cref="IComputeGateway.FindByJobIdAsync"/>
/// before ever attempting a zone); this file isolates the gateway-level
/// behaviour the fix depends on.
/// </summary>
public class FakeGcpRetrySafetyTests
{
    private const string Zone = "us-central1-a";

    private static VmSpec ValidSpec(string jobId) => new(
        ProjectId: "my-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    [Fact]
    public async Task Replaying_the_insert_of_a_job_with_FakeGcp_yields_exactly_one_instance()
    {
        // Issue #257's own observable, verbatim.
        var gcp = new FakeGcp();
        var spec = ValidSpec("job-1");

        var first = await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        var second = await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);

        second.ShouldBe(first);
        (await gcp.FindByJobIdAsync("job-1", CancellationToken.None)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_replayed_insert_does_not_re_run_project_wide_checks()
    {
        // Requester-id dedup means Compute Engine returns the ORIGINAL
        // operation's result - it does not re-validate billing/permission
        // on the replay. Script billing-off only AFTER the first create
        // already succeeded, to prove the second call doesn't re-check it.
        var gcp = new FakeGcp();
        var spec = ValidSpec("job-2");
        var first = await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);

        gcp.WithBillingOff("my-project");
        var second = await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task MEASURED_a_second_zones_create_for_the_same_job_succeeds_independently_reproducing_issue_389()
    {
        // MEASURED (against this fake, 2026-09-19): issue #389's THEORY -
        // "a VM name is unique only within a zone, so a crash between zone
        // attempts could create two VMs with the same name in different
        // zones" - is not merely plausible, it is exactly what this
        // gateway-level call sequence produces when nothing reconciles
        // first. This is why CloudJobRunner.ProvisionAsync calls
        // FindByJobIdAsync BEFORE attempting any zone, never after.
        var gcp = new FakeGcp();
        var spec = ValidSpec("job-3");

        var inZoneA = await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);
        var inZoneB = await gcp.CreateVmAsync(spec, "us-central1-b", CancellationToken.None);

        inZoneA.Zone.ShouldBe("us-central1-a");
        inZoneB.Zone.ShouldBe("us-central1-b");
        var all = await gcp.FindByJobIdAsync("job-3", CancellationToken.None);
        all.Count.ShouldBe(2, "this is the leak #389 describes, reproduced deliberately, not a test bug");
    }

    [Fact]
    public async Task WithAlreadyExists_still_throws_on_every_attempt_unlike_a_normal_replay()
    {
        var gcp = new FakeGcp().WithAlreadyExists("deg-job-4", Zone);
        var spec = ValidSpec("job-4");

        await Should.ThrowAsync<CloudOperationException>(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));
        var second = await Should.ThrowAsync<CloudOperationException>(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));

        second.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task FindByJobIdAsync_returns_empty_for_a_job_with_no_vm_anywhere()
    {
        var gcp = new FakeGcp();

        (await gcp.FindByJobIdAsync("no-such-job", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetProjectStateAsync_defaults_to_active_and_honours_a_scripted_state()
    {
        var gcp = new FakeGcp().WithProjectState("bad-project", ProjectLifecycleState.NotFound);

        (await gcp.GetProjectStateAsync("my-project", CancellationToken.None)).ShouldBe(ProjectLifecycleState.Active);
        (await gcp.GetProjectStateAsync("bad-project", CancellationToken.None)).ShouldBe(ProjectLifecycleState.NotFound);
    }
}
