using System.Diagnostics;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Cold-review findings on the zone ladder (#670), each written from the symptom: a crash mid-batch that leaks a VM on resume, a winner
/// that waits for the slowest zone, a listing that lags, a quota guess that hides a stockout, a pinned zone that is walked away from.
/// </summary>
public class ZoneLadderReviewTests
{
    private const string Project = "my-project";

    private static readonly string[] Offered =
    [
        "us-central1-a", "us-central1-b", "us-central1-c", "us-central1-f",
        "us-east1-b", "us-east1-c", "us-east1-d",
        "europe-west4-a", "europe-west4-b",
    ];

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

    private static VmProvisioner Provisioner(IComputeGateway compute, FakeGcp gcp, Func<RunProgress, string, CancellationToken, Task>? claimZone = null)
    {
        var settings = new CloudRunSettings
        {
            CreateTimeout = TimeSpan.FromSeconds(5),
            CreateSettleTimeout = TimeSpan.FromSeconds(3),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            LifecycleTimeout = TimeSpan.FromMilliseconds(100),
        };
        var calls = new GatewayCalls(settings, gcp);
        return new VmProvisioner(compute, calls, settings, new VmTerminator(compute, calls, settings), null, gcp, null, claimZone);
    }

    private static CloudJobRequest Request(string jobId, VmSpec spec, string? zonePreference = null)
    {
        var request = TestInputs.Request(jobId, spec, zones: zonePreference is null ? ["us-central1-a"] : [zonePreference, "us-central1-a"]);
        return zonePreference is null ? request : request with { Options = request.Options with { ZonePreference = zonePreference } };
    }

    private sealed class Stub(FakeGcp inner) : IComputeGateway
    {
        public Func<VmSpec, string, CancellationToken, Task<VmDescriptor>>? OnCreate { get; set; }

        public Func<string, CancellationToken, Task<IReadOnlyList<VmDescriptor>>>? OnFind { get; set; }

        public Func<string, string, CancellationToken, Task>? OnDelete { get; set; }

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
            => OnCreate is null ? inner.CreateVmAsync(spec, zone, cancellationToken) : OnCreate(spec, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
            => OnDelete is null ? inner.DeleteVmAsync(vmName, zone, cancellationToken) : OnDelete(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
            => OnFind is null ? inner.FindByJobIdAsync(jobId, cancellationToken) : OnFind(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }

    private static async Task<string[]> LiveZonesAsync(FakeGcp gcp, string jobId)
    {
        var spec = Spec(jobId);
        var zones = new List<string>();
        foreach (var zone in Offered)
        {
            if (await gcp.GetVmAsync(spec.VmName, zone, CancellationToken.None) is not null)
            {
                zones.Add(zone);
            }
        }

        return [.. zones];
    }

    // ---- 4: a resume that finds two VMs for one job keeps one ----

    [Fact]
    public async Task A_resume_that_finds_two_VMs_labelled_with_the_job_keeps_one_and_deletes_the_other()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered);
        var spec = Spec("job-d1");
        await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);
        await gcp.CreateVmAsync(spec, "us-east1-b", CancellationToken.None);

        var progress = new RunProgress();
        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-d1", spec), spec, progress, CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        var live = await LiveZonesAsync(gcp, "job-d1");
        live.Length.ShouldBe(1, "the crashed batch left a second VM: it must be deleted, not adopted around");
        result.Zone.ShouldBe(live[0], "the run must continue on the VM that was kept");
        gcp.CreateAttempts.ShouldBe(2, "the resume must not create a third");
    }

    [Fact]
    public async Task A_resume_prefers_the_VM_that_is_running_over_one_that_is_stopped()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered);
        var spec = Spec("job-d2");
        await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);
        await gcp.StopVmAsync(spec.VmName, "us-central1-a", CancellationToken.None);
        await gcp.CreateVmAsync(spec, "us-east1-b", CancellationToken.None);

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-d2", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("us-east1-b");
        (await LiveZonesAsync(gcp, "job-d2")).ShouldBe(["us-east1-b"]);
    }

    [Fact]
    public async Task A_resume_whose_extra_VM_cannot_be_deleted_says_so()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered);
        var spec = Spec("job-d3");
        await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);
        await gcp.CreateVmAsync(spec, "us-east1-b", CancellationToken.None);
        var stub = new Stub(gcp)
        {
            OnDelete = (name, zone, token) => throw new CloudOperationException(new CloudError("INTERNAL", 500, "boom"), CloudErrorKind.Other),
        };

        var result = await Provisioner(stub, gcp).ProvisionAsync(Request("job-d3", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse("a second VM that may still be billing is not a quiet success");
        result.VmEndNote.ShouldNotBeNull();
        result.VmEndNote.ShouldContain("not confirmed");
    }

    [Fact]
    public async Task A_run_that_finds_its_result_already_there_with_two_VMs_leaves_only_one()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered);
        var spec = Spec("job-d4");
        var request = TestInputs.Request("job-d4", spec);
        gcp.PutObject(FakeGcp.BucketName(Project), "jobs/job-d4/manifest.json", """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray());
        await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);
        await gcp.CreateVmAsync(spec, "us-east1-b", CancellationToken.None);
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-d4", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(5),
            LifecycleTimeout = TimeSpan.FromSeconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        (await LiveZonesAsync(gcp, "job-d4")).Length.ShouldBe(1, "the finished run's other VM was left behind");
    }

    // ---- 7: act on the first winner; stragglers are cancelled and cleaned ----

    private static FakeGcp ThreeStockedOut()
        => new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered).WithZoneStockout("us-central1-a").WithZoneStockout("us-central1-b").WithZoneStockout("us-central1-c");

    [Fact]
    public async Task The_first_winner_is_kept_at_once_without_waiting_for_the_slowest_zone()
    {
        var gcp = ThreeStockedOut();
        var slow = new VirtualTimeProvider();
        var stub = new Stub(gcp)
        {
            OnCreate = async (spec, zone, token) =>
            {
                if (zone is "us-east1-b" or "us-east1-c")
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), slow, token);
                }

                return await gcp.CreateVmAsync(spec, zone, token);
            },
        };
        var spec = Spec("job-w1");
        var started = slow.GetUtcNow();

        // The slow zones sit on a virtual clock nobody advances: if the ladder waited for them it would never return (the guard below is a hang
        // guard, not a speed assertion).
        var result = await Provisioner(stub, gcp).ProvisionAsync(Request("job-w1", spec), spec, new RunProgress(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("us-central1-f");
        slow.GetUtcNow().ShouldBe(started, "the winner waited for the slowest attempt");
        (await LiveZonesAsync(gcp, "job-w1")).ShouldBe(["us-central1-f"]);
    }

    [Fact]
    public async Task A_straggler_whose_create_lands_after_the_winner_is_deleted_before_the_ladder_returns()
    {
        var gcp = ThreeStockedOut();
        var stub = new Stub(gcp)
        {
            OnCreate = async (spec, zone, token) =>
            {
                if (zone == "us-east1-b")
                {
                    await Task.Delay(400, CancellationToken.None);
                    return await gcp.CreateVmAsync(spec, zone, CancellationToken.None);
                }

                return await gcp.CreateVmAsync(spec, zone, token);
            },
        };
        var spec = Spec("job-w2");

        var result = await Provisioner(stub, gcp).ProvisionAsync(Request("job-w2", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("us-central1-f");
        (await LiveZonesAsync(gcp, "job-w2")).ShouldBe(["us-central1-f"], "an extra that landed late must not be left billing");
    }

    [Fact]
    public async Task A_parallel_win_records_the_zone_it_kept_so_an_extra_VM_that_boots_anyway_can_refuse_to_run()
    {
        var gcp = ThreeStockedOut();
        var claimed = new List<string>();
        var spec = Spec("job-w3");

        var result = await Provisioner(gcp, gcp, (_, zone, _) =>
        {
            claimed.Add(zone);
            return Task.CompletedTask;
        }).ProvisionAsync(Request("job-w3", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        claimed.TakeLast(2).ToList().ShouldBe(["", result.Zone!], "a parallel batch clears any stale claim before it creates anything, then writes the kept zone for the VMs' startup script to read before it touches the job prefix");
    }

    [Fact]
    public async Task A_single_attempt_writes_its_zone_before_the_create_so_a_stale_claim_cannot_delete_the_VM()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered);
        var claimed = new List<string>();
        var spec = Spec("job-w4");

        var result = await Provisioner(gcp, gcp, (_, zone, _) =>
        {
            claimed.Add(zone);
            return Task.CompletedTask;
        }).ProvisionAsync(Request("job-w4", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        claimed.ShouldBe([result.Zone!], "an earlier parallel win left a claim naming another zone: the VM would read it and delete itself");
    }

    // ---- 8: extras come from the zones the batch knows, not from a listing that can lag ----

    [Fact]
    public async Task Extras_are_deleted_from_the_zones_the_batch_landed_in_even_when_the_label_listing_lags()
    {
        var gcp = ThreeStockedOut();
        var stub = new Stub(gcp) { OnFind = (_, _) => Task.FromResult<IReadOnlyList<VmDescriptor>>([]) };
        var spec = Spec("job-l1");

        var result = await Provisioner(stub, gcp).ProvisionAsync(Request("job-l1", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        (await LiveZonesAsync(gcp, "job-l1")).Length.ShouldBe(1, "the listing said nothing, so the extras were never found");
    }

    [Fact]
    public async Task Every_extra_that_cannot_be_deleted_is_recorded_not_just_the_first()
    {
        var gcp = ThreeStockedOut();
        var stub = new Stub(gcp)
        {
            OnFind = (_, _) => Task.FromResult<IReadOnlyList<VmDescriptor>>([]), // a lagging listing: only the batch's own zones can name the extras
            OnDelete = (name, zone, token) => zone.StartsWith("us-east1", StringComparison.Ordinal)
                ? throw new CloudOperationException(new CloudError("INTERNAL", 500, "boom"), CloudErrorKind.Other)
                : gcp.DeleteVmAsync(name, zone, token),
        };
        var spec = Spec("job-l2");

        var result = await Provisioner(stub, gcp).ProvisionAsync(Request("job-l2", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.VmEndNote.ShouldNotBeNull();
        result.VmEndNote.ShouldContain("us-east1-b");
        result.VmEndNote.ShouldContain("us-east1-c");
    }

    // ---- 9: the create results decide stockout versus quota ----

    [Fact]
    public async Task A_quota_read_of_zero_does_not_turn_real_stockouts_into_a_quota_error()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered).WithAllRegionsGpuCap(0);
        foreach (var zone in Offered)
        {
            gcp.WithZoneStockout(zone);
        }

        var spec = Spec("job-q1");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-q1", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Stockout, "every zone answered 'no capacity': waiting helps, raising a quota does not");
    }

    // ---- 10: a zone the user pinned is a constraint, not a preference ----

    [Fact]
    public async Task A_pinned_zone_is_never_walked_away_from()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered).WithZoneStockout("europe-west4-a");
        var spec = Spec("job-p1");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-p1", spec, "europe-west4-a"), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.FailureKind.ShouldBe(CloudErrorKind.Stockout);
        gcp.CreateAttempts.ShouldBe(1, "the user chose europe-west4-a (data residency): a create in a us-* zone is not what they asked for");
        (await LiveZonesAsync(gcp, "job-p1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pinned_zone_that_has_capacity_is_used()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered);
        var spec = Spec("job-p2");

        var result = await Provisioner(gcp, gcp).ProvisionAsync(Request("job-p2", spec, "europe-west4-a"), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        result.Zone.ShouldBe("europe-west4-a");
    }

    // ---- review: the claim is written the moment the winner is chosen, and every provision overwrites a stale one ----

    [Fact]
    public async Task The_claim_is_written_before_any_extra_is_deleted()
    {
        var gcp = ThreeStockedOut();
        var events = new List<string>();
        var stub = new Stub(gcp)
        {
            OnDelete = (name, zone, token) =>
            {
                events.Add("delete:" + zone);
                return gcp.DeleteVmAsync(name, zone, token);
            },
        };
        var spec = Spec("job-o1");

        var result = await Provisioner(stub, gcp, (_, zone, _) =>
        {
            events.Add("claim:" + zone);
            return Task.CompletedTask;
        }).ProvisionAsync(Request("job-o1", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeTrue(result.FailureMessage);
        events.Any(e => e.StartsWith("delete:", StringComparison.Ordinal)).ShouldBeTrue("the scenario must leave extras to delete, or this test proves nothing");
        events.IndexOf("claim:" + result.Zone).ShouldBeGreaterThanOrEqualTo(0);
        events.IndexOf("claim:" + result.Zone).ShouldBeLessThan(events.FindIndex(e => e.StartsWith("delete:", StringComparison.Ordinal)), "an extra mid-boot reads the claim: it must exist before the first delete");
    }

    [Fact]
    public async Task The_claim_is_written_even_when_an_extra_cannot_be_confirmed_gone()
    {
        var gcp = ThreeStockedOut();
        var claimed = new List<string>();
        var stub = new Stub(gcp) { OnDelete = (_, _, _) => throw new CloudOperationException(new CloudError("PERMISSION_DENIED", 403, "no"), CloudErrorKind.Permission) };
        var spec = Spec("job-o2");

        var result = await Provisioner(stub, gcp, (_, zone, _) =>
        {
            claimed.Add(zone);
            return Task.CompletedTask;
        }).ProvisionAsync(Request("job-o2", spec), spec, new RunProgress(), CancellationToken.None);

        result.Success.ShouldBeFalse("an extra that cannot be deleted is reported");
        claimed.LastIndexOf(string.Empty).ShouldBeLessThan(claimed.Count - 1, "the parallel batch cleared the claim, then the winner must have been written after it; the one case the claim exists for: an extra that is still alive must be told to stand down");
    }

    private static CloudJobRunner ClaimRunner(FakeGcp gcp, IStorageGateway? storage = null) => new(gcp, storage ?? gcp, gcp, gcp, gcp, new InMemoryRunRepository())
    {
        ResultPollInterval = TimeSpan.FromMilliseconds(1),
        ResultTimeout = TimeSpan.FromSeconds(10),
        CallTimeout = TimeSpan.FromMilliseconds(300),
        LifecycleTimeout = TimeSpan.FromSeconds(1),
        LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task A_stale_claim_from_an_earlier_parallel_win_does_not_survive_a_single_zone_provision()
    {
        var gcp = new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered).WithWorker(FakeWorkerMode.Done);
        var bucket = FakeGcp.BucketName(Project);
        using (var stale = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("us-east1-b")))
        {
            await gcp.UploadAsync(bucket, "jobs/job-s1/control/zone", stale, CancellationToken.None);
        }

        var result = await ClaimRunner(gcp).RunAsync(Request("job-s1", Spec("job-s1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var live = await LiveZonesAsync(gcp, "job-s1");
        live.ShouldBe(["us-central1-a"], "a single-zone provision in us-central1-a");
        System.Text.Encoding.UTF8.GetString(gcp.GetObjectBytes(bucket, "jobs/job-s1/control/zone")).ShouldBe("us-central1-a", "the VM reads this at boot: a stale zone would make it delete itself");
    }

    private sealed class ClaimHangsStorage(FakeGcp inner) : IStorageGateway
    {
        public Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken) => inner.EnsureBucketAsync(projectId, cancellationToken);

        public async Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
        {
            if (objectKey.EndsWith("control/zone", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            await inner.UploadAsync(bucket, objectKey, content, cancellationToken).ConfigureAwait(false);
        }

        public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken) => inner.DownloadAsync(bucket, objectKey, cancellationToken);

        public Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken) => inner.TryDownloadAsync(bucket, objectKey, cancellationToken);

        public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(string projectId, CancellationToken cancellationToken) => inner.ListBucketsAsync(projectId, cancellationToken);

        public Task<PrefixUsage> GetPrefixUsageAsync(string bucket, string prefix, CancellationToken cancellationToken) => inner.GetPrefixUsageAsync(bucket, prefix, cancellationToken);

        public Task<IReadOnlyList<string>> ListChildPrefixesAsync(string bucket, string prefix, CancellationToken cancellationToken) => inner.ListChildPrefixesAsync(bucket, prefix, cancellationToken);

        public Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken) => inner.DeletePrefixAsync(bucket, prefix, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_claim_write_that_hangs_past_its_call_timeout_never_fails_the_run(bool parallel)
    {
        // GatewayCalls turns a call that outlives its deadline into a Network CloudOperationException; the claim is best effort and swallows it.
        var gcp = (parallel ? ThreeStockedOut() : new FakeGcp().WithAcceleratorZones("nvidia-l4", Offered)).WithWorker(FakeWorkerMode.Done);

        var result = await ClaimRunner(gcp, new ClaimHangsStorage(gcp)).RunAsync(Request("job-h1", Spec("job-h1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }
    [Fact]
    public async Task The_runner_writes_the_kept_zone_into_the_job_prefix_after_a_parallel_win()
    {
        var gcp = ThreeStockedOut().WithWorker(FakeWorkerMode.Done);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(10),
            LifecycleTimeout = TimeSpan.FromSeconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = await runner.RunAsync(Request("job-c1", Spec("job-c1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var live = await LiveZonesAsync(gcp, "job-c1");
        live.Length.ShouldBe(1);
        System.Text.Encoding.UTF8.GetString(gcp.GetObjectBytes(FakeGcp.BucketName(Project), "jobs/job-c1/control/zone")).ShouldBe(live[0]);
    }
}
