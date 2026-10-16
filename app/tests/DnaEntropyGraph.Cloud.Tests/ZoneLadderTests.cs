using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #670 (#86b): the zone ladder over <see cref="ZoneCandidates"/>. The zone universe is the accelerator offer read on the
/// gateway, narrowed by the quota read; the attempts are 3 sequential, then 3, 5 and 7 parallel with the same VM name; extra winners are
/// deleted; a project-wide error aborts at its first sighting; the tier is never escalated without a click. Each test asserts what is
/// left in the fake project, because a ladder that leaves two VMs billing is the bug.
/// </summary>
public class ZoneLadderTests
{
    private static readonly string[] Offered =
    [
        "us-central1-a", "us-central1-b", "us-central1-c", "us-central1-f",
        "us-east1-b", "us-east1-c", "us-east1-d",
        "europe-west4-a", "europe-west4-b",
    ];

    private static VmSpec Spec(string jobId, string machine = "g2-standard-8") => new(
        ProjectId: "my-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: machine,
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static VmProvisioner Provisioner(IComputeGateway compute, FakeGcp gcp)
    {
        var settings = new CloudRunSettings
        {
            CreateTimeout = TimeSpan.FromSeconds(5),
            CreateSettleTimeout = TimeSpan.FromMilliseconds(200),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(100),
        };
        var calls = new GatewayCalls(settings, gcp);
        return new VmProvisioner(compute, calls, settings, new VmTerminator(compute, calls, settings), null, gcp);
    }

    private static CloudJobRequest Request(string jobId, VmSpec spec) => TestInputs.Request(jobId, spec, zones: ["us-central1-a"]);

    /// <summary>The fake behind a gateway that records what the ladder asked and how many creates were in flight together.</summary>
    private sealed class Recording(FakeGcp inner, TimeSpan createTime) : IComputeGateway
    {
        private int _inflight;

        public List<string> OfferReads { get; } = [];

        public List<int> InflightBeforeEachCreate { get; } = [];

        public int PeakInflight { get; private set; }

        public CloudOperationException? OfferFailure { get; init; }

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken)
        {
            lock (OfferReads)
            {
                OfferReads.Add(acceleratorType);
            }

            return OfferFailure is null ? inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken) : Task.FromException<IReadOnlyList<string>>(OfferFailure);
        }

        public async Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
        {
            int before;
            lock (InflightBeforeEachCreate)
            {
                before = _inflight++;
                InflightBeforeEachCreate.Add(before);
                PeakInflight = Math.Max(PeakInflight, _inflight);
            }

            try
            {
                await Task.Delay(createTime, cancellationToken).ConfigureAwait(false);
                return await inner.CreateVmAsync(spec, zone, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (InflightBeforeEachCreate)
                {
                    _inflight--;
                }
            }
        }

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);
    }

    [Fact]
    public async Task With_stockout_in_the_first_four_zones_the_fifth_wins_and_exactly_one_instance_remains()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered)
            .WithZoneStockout("us-central1-a").WithZoneStockout("us-central1-b").WithZoneStockout("us-central1-c").WithZoneStockout("us-central1-f");
        var spec = Spec("job-z1");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-z1", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("us-east1-b", "the first of the zones that answered in the race wins, by the candidate order");
        var remaining = await gcp.FindByJobIdAsync("job-z1", CancellationToken.None);
        remaining.Select(v => v.Zone).ShouldBe(["us-east1-b"], "the parallel race made extra VMs: they must be deleted, leaving exactly one");
    }

    [Fact]
    public async Task The_ladder_runs_3_sequential_then_3_5_and_7_parallel_attempts_over_the_candidate_zones()
    {
        var zones = Enumerable.Range(1, 20).Select(i => $"us-central1-z{i:00}").ToArray();
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", zones);
        foreach (var zone in zones)
        {
            gcp.WithZoneStockout(zone);
        }

        var recording = new Recording(gcp, TimeSpan.FromMilliseconds(60));
        var spec = Spec("job-z2");

        var result = await Provisioner(recording, gcp).ProvisionAsync(Request("job-z2", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Stockout);
        recording.InflightBeforeEachCreate.Count.ShouldBe(20, "every candidate zone is tried before the ladder gives up");

        // A batch starts when nothing was in flight: its size is the run of attempts until the next such start.
        var sizes = new List<int>();
        foreach (var before in recording.InflightBeforeEachCreate)
        {
            if (before == 0)
            {
                sizes.Add(1);
            }
            else
            {
                sizes[^1]++;
            }
        }

        sizes.ShouldBe([1, 1, 1, 3, 5, 7, 2]);
        recording.PeakInflight.ShouldBe(7);
    }

    [Fact]
    public async Task A_project_wide_error_aborts_the_whole_ladder_at_its_first_sighting()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered).WithBillingOff("my-project");
        var spec = Spec("job-z3");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-z3", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Billing);
        gcp.CreateAttempts.ShouldBe(1, "billing off cannot be fixed by another zone");
    }

    [Fact]
    public async Task Regions_with_no_quota_are_never_tried_while_another_region_has_some()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered)
            .WithGpuQuota("us-central1", "nvidia-l4", 0).WithGpuQuota("us-east1", "nvidia-l4", 1).WithGpuQuota("europe-west4", "nvidia-l4", 0);
        var spec = Spec("job-z4");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-z4", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("us-east1-b");
        gcp.CreateAttempts.ShouldBe(1, "quota is per region and pre-filterable: a region at zero is not worth an insert");
    }

    [Fact]
    public async Task A_quota_that_cannot_be_read_does_not_narrow_the_zones()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered).WithQuotaReadFailing(CloudErrorKind.Network)
            .WithZoneStockout("us-central1-a").WithZoneStockout("us-central1-b").WithZoneStockout("us-central1-c");
        var spec = Spec("job-z5");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-z5", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("us-central1-f", "unknown is not zero: every offered zone stays a candidate");
    }

    [Fact]
    public async Task Quota_at_zero_everywhere_still_tries_every_zone_then_reports_the_fixable_quota_not_a_stockout()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered).WithAllRegionsGpuCap(0);
        foreach (var zone in Offered)
        {
            gcp.WithQuotaExceededOnCreate(zone);
        }

        var spec = Spec("job-z6");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-z6", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Quota, "the user can fix a quota; a stockout message would send them to wait for nothing");
        result.FailureCode.ShouldBe("quota");
        gcp.CreateAttempts.ShouldBe(Offered.Length, "the quota API can under-report, so every zone is still tried");
    }

    [Fact]
    public async Task Only_the_accelerator_of_the_requested_machine_is_asked_for_and_no_other_tier_is_tried()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", "us-central1-a").WithAcceleratorZones("nvidia-tesla-a100", "us-central1-b", "us-central1-c").WithZoneStockout("us-central1-a");
        var recording = new Recording(gcp, TimeSpan.Zero);
        var spec = Spec("job-z7");

        var result = await Provisioner(recording, gcp).ProvisionAsync(Request("job-z7", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Stockout);
        recording.OfferReads.ShouldBe(["nvidia-l4"]);
        gcp.CreateAttempts.ShouldBe(1, "a bigger GPU is a roughly 4x price jump and needs the user's click, never an automatic fallback");
    }

    [Fact]
    public async Task An_offer_read_that_fails_falls_back_to_the_requested_zones_instead_of_failing_the_run()
    {
        var gcp = new FakeGcp();
        var recording = new Recording(gcp, TimeSpan.Zero)
        {
            OfferFailure = new CloudOperationException(new CloudError("TIMEOUT", null, "request timed out"), CloudErrorKind.Network),
        };
        var spec = Spec("job-z8");

        var result = await Provisioner(recording, gcp).ProvisionAsync(Request("job-z8", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("us-central1-a");
    }

    [Fact]
    public async Task A_machine_with_no_GPU_never_reads_an_accelerator_offer()
    {
        var gcp = new FakeGcp();
        var recording = new Recording(gcp, TimeSpan.Zero);
        var spec = Spec("job-z9", "e2-standard-4");

        var result = await Provisioner(recording, gcp).ProvisionAsync(Request("job-z9", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        recording.OfferReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stockout_that_landed_a_VM_in_one_parallel_zone_does_not_delete_the_winner_in_another()
    {
        // A failed attempt cleans up ITS zone only: a sweep by job label would delete the race's winner.
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", "us-central1-a", "us-central1-b", "us-central1-c", "us-central1-f", "us-east1-b", "us-east1-c")
            .WithZoneStockout("us-central1-a").WithZoneStockout("us-central1-b").WithZoneStockout("us-central1-c").WithZoneStockout("us-central1-f").WithZoneStockout("us-east1-c");
        var spec = Spec("job-z10");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-z10", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        (await gcp.FindByJobIdAsync("job-z10", CancellationToken.None)).Select(v => v.Zone).ShouldBe(["us-east1-b"]);
    }
}
