using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>The zone ladder and the in-flight create state, alone, over <see cref="FakeGcp"/>.</summary>
public class VmProvisionerTests
{
    private const string Project = "my-project";

    private static VmSpec Spec(string jobId) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static CloudRunSettings Settings() => new()
    {
        CreateTimeout = TimeSpan.FromSeconds(5),
        CreateSettleTimeout = TimeSpan.FromMilliseconds(100),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        LifecycleTimeout = TimeSpan.FromMilliseconds(50),
    };

    private static VmProvisioner Provisioner(FakeGcp gcp)
    {
        var settings = Settings();
        var calls = new GatewayCalls(settings, gcp);
        return new VmProvisioner(gcp, calls, settings, new VmTerminator(gcp, calls, settings));
    }

    [Fact]
    public async Task The_ladder_walks_zones_in_the_order_given_and_stops_at_the_first_that_creates()
    {
        var gcp = new FakeGcp().WithZoneStockout("us-central1-a").WithZoneStockout("us-central1-b");
        var spec = Spec("job-p1");
        var request = TestInputs.Request("job-p1", spec, zones: ["us-central1-a", "us-central1-b", "us-central1-c", "us-central1-f"]);

        var result = await Provisioner(gcp).ProvisionAsync(request, spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Zone.ShouldBe("us-central1-c");
        gcp.CreateAttempts.ShouldBe(3);
    }

    [Fact]
    public async Task A_create_slower_than_CreateTimeout_is_cut_off_at_CreateTimeout_and_the_late_VM_is_deleted()
    {
        // Pins that CreateTimeout (not the poller's deadline, which is the same value) is what ends the wait: the call is cut when the
        // clock reads 100 ms although the create would take 400 ms, and what the create then made is deleted. The fake and the provisioner
        // share one virtual clock (#567) that this test moves itself, 10 ms at a time, so the moment the provisioner gives up is the
        // moment it is measured at: it must not have armed its settle window before the deadline, and must have by the deadline.
        var clocks = new RunnerClocks();
        var gcp = new FakeGcp(clocks.Run).WithCreateDelay(TimeSpan.FromMilliseconds(400));
        var spec = Spec("job-p9");
        var request = TestInputs.Request("job-p9", spec, zones: ["us-central1-a"]);
        var deadline = TimeSpan.FromMilliseconds(100);
        var step = TimeSpan.FromMilliseconds(10);
        var settleWindow = TimeSpan.FromSeconds(5);
        var settings = new CloudRunSettings
        {
            CreateTimeout = deadline,
            CreateSettleTimeout = settleWindow,
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(50),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };
        var calls = new GatewayCalls(settings, gcp);
        var provisioner = new VmProvisioner(gcp, calls, settings, new VmTerminator(gcp, calls, settings));

        var create = provisioner.ProvisionAsync(request, spec, new RunProgress(), CancellationToken.None);
        await clocks.Run.WhenTimerCreatedAsync(0, deadline).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var settleArmed = clocks.Run.WhenTimerCreatedAsync(0, settleWindow);
        var started = clocks.Run.GetTimestamp();
        while (clocks.Run.GetElapsedTime(started) < deadline)
        {
            settleArmed.IsCompleted.ShouldBeFalse("the provisioner gave up on the create before its deadline");
            clocks.Run.Advance(step);
        }

        await settleArmed.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var cutAt = clocks.Run.GetElapsedTime(started);
        cutAt.ShouldBeGreaterThanOrEqualTo(deadline);
        cutAt.ShouldBeLessThanOrEqualTo(deadline + step, "the wait ended at CreateTimeout, not at the create's own landing");
        clocks.Run.Advance(TimeSpan.FromMilliseconds(400) - cutAt);
        var result = await create.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        (await gcp.FindByJobIdAsync("job-p9", CancellationToken.None)).ShouldBeEmpty("the late create must be settled and deleted");
    }

    [Fact]
    public async Task An_existing_VM_for_the_job_is_adopted_before_any_zone_is_tried()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-p2");
        await gcp.CreateVmAsync(spec, "us-east1-b", CancellationToken.None);
        var attemptsBefore = gcp.CreateAttempts;
        var request = TestInputs.Request("job-p2", spec, zones: ["us-central1-a"]);
        var progress = new RunProgress();

        var result = await Provisioner(gcp).ProvisionAsync(request, spec, progress, CancellationToken.None);

        result.Zone.ShouldBe("us-east1-b");
        gcp.CreateAttempts.ShouldBe(attemptsBefore);
        progress.VmMayExist.ShouldBeTrue();
    }

    [Fact]
    public async Task Every_zone_stocked_out_is_a_stockout_that_names_the_zones()
    {
        var gcp = new FakeGcp().WithZoneStockout("us-central1-a").WithZoneStockout("us-central1-b");
        var spec = Spec("job-p3");
        var request = TestInputs.Request("job-p3", spec, zones: ["us-central1-a", "us-central1-b"]);

        var result = await Provisioner(gcp).ProvisionAsync(request, spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Stockout);
        result.FailureMessage!.ShouldContain("us-central1-a, us-central1-b");
    }

    [Fact]
    public async Task Settling_with_nothing_in_flight_is_immediately_true()
        => (await Provisioner(new FakeGcp()).SettleInflightCreatesAsync("job-p4")).ShouldBeTrue();

    [Fact]
    public async Task A_create_still_in_flight_past_the_settle_limit_is_not_settled_and_lands_later()
    {
        var gcp = new FakeGcp().WithBlockedCreate();
        var spec = Spec("job-p5");
        var provisioner = Provisioner(gcp);
        var request = TestInputs.Request("job-p5", spec, zones: ["us-central1-a"]);
        using var cancel = new CancellationTokenSource();

        // The ladder gives up on the blocked create (cancel) while the request is still in flight inside the fake.
        var ladder = provisioner.ProvisionAsync(request, spec, new RunProgress(), cancel.Token);
        await gcp.CreateVmEntered;
        (await provisioner.SettleInflightCreatesAsync("job-p5").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await provisioner.SettleInflightCreatesAsync("another-job")).ShouldBeTrue();

        gcp.ReleaseCreate();
        await ladder;
        (await provisioner.SettleInflightCreatesAsync("job-p5")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("g2-standard-8", true)]
    [InlineData("a2-highgpu-1g", true)]
    [InlineData("a3-highgpu-8g", true)]
    [InlineData("e2-standard-4", false)]
    public void Only_the_GPU_families_expect_a_GPU(string machineType, bool expected)
        => VmProvisioner.ExpectsGpu(machineType).ShouldBe(expected);
}
