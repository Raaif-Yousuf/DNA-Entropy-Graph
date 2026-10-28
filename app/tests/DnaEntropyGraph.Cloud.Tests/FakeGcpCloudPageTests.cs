using System.Text;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #105: the gateway methods the Cloud page stands on, proven on the fake first. The fake must behave like the real
/// gateways (label-filtered listings, a start that needs a stopped VM, a delete that can fail or be ignored) or every later test
/// that leans on it proves nothing.
/// </summary>
public class FakeGcpCloudPageTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId, string installation = "install-1") => new(
        ProjectId: Project,
        InstallationId: installation,
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static DiskDescriptor Disk(string name, string installation = "install-1", int sizeGb = 150, string status = "READY", params string[] attachedTo) => new(
        name,
        Zone,
        sizeGb,
        status,
        new Dictionary<string, string> { ["app"] = VmSpec.AppLabelValue, ["installation-id"] = installation, ["job-id"] = "job-x" },
        DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
        attachedTo);

    private static Task PutAsync(FakeGcp gcp, string bucket, string key, string content)
    {
        gcp.PutObject(bucket, key, Encoding.UTF8.GetBytes(content));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- StartVmAsync

    [Fact]
    public async Task StartVm_turns_a_stopped_VM_into_a_running_one_and_moves_its_start_time()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job1");
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        await gcp.StopVmAsync(spec.VmName, Zone, CancellationToken.None);

        await gcp.StartVmAsync(spec.VmName, Zone, CancellationToken.None);

        var vm = await gcp.GetVmAsync(spec.VmName, Zone, CancellationToken.None);
        vm!.Status.ShouldBe("RUNNING");
        vm.StartedAt.ShouldNotBeNull();
        vm.StoppedAt.ShouldBeNull();
        vm.Labels!["job-id"].ShouldBe("job1", "a plain start never rewrites the labels (that is what reuse does)");
    }

    [Fact]
    public async Task StartVm_of_a_VM_that_does_not_exist_is_a_not_found_error_not_a_silent_success()
    {
        var gcp = new FakeGcp();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.StartVmAsync("deg-gone", Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe("VM_NOT_FOUND");
        ex.Error.HttpStatus.ShouldBe(404);
    }

    [Fact]
    public async Task StartVm_of_a_stockout_zone_is_classified_as_a_stockout()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job1");
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        await gcp.StopVmAsync(spec.VmName, Zone, CancellationToken.None);
        gcp.WithStartFailure(CloudErrorKind.Stockout);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.StartVmAsync(spec.VmName, Zone, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Stockout);
        (await gcp.GetVmAsync(spec.VmName, Zone, CancellationToken.None))!.Status.ShouldNotBe("RUNNING", "a failed start leaves the VM stopped");
    }

    [Theory]
    [InlineData(CloudErrorKind.Billing)]
    [InlineData(CloudErrorKind.Permission)]
    [InlineData(CloudErrorKind.Quota)]
    public async Task StartVm_failure_kinds_come_through_as_the_scripted_kind(CloudErrorKind kind)
    {
        var gcp = new FakeGcp();
        var spec = Spec("job1");
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        await gcp.StopVmAsync(spec.VmName, Zone, CancellationToken.None);
        gcp.WithStartFailure(kind);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.StartVmAsync(spec.VmName, Zone, CancellationToken.None));

        ex.Kind.ShouldBe(kind);
    }

    // ---------------------------------------------------------------- disks

    [Fact]
    public async Task ListDisksByInstallation_returns_only_this_installations_labelled_disks_with_size_and_status()
    {
        var gcp = new FakeGcp()
            .WithDisk(Disk("deg-job1", "install-1", 150, "READY"))
            .WithDisk(Disk("deg-job2", "install-2"))
            .WithDisk(new DiskDescriptor("unlabelled", Zone, 10, "READY", new Dictionary<string, string>()))
            .WithDisk(new DiskDescriptor("wrong-app", Zone, 10, "READY", new Dictionary<string, string> { ["app"] = "someone-else", ["installation-id"] = "install-1" }));

        var disks = await gcp.ListDisksByInstallationAsync("install-1", CancellationToken.None);

        var disk = disks.ShouldHaveSingleItem();
        disk.Name.ShouldBe("deg-job1");
        disk.SizeGb.ShouldBe(150);
        disk.Status.ShouldBe("READY");
        disk.Zone.ShouldBe(Zone);
    }

    [Fact]
    public async Task DeleteDisk_removes_exactly_that_disk()
    {
        var gcp = new FakeGcp().WithDisk(Disk("deg-a")).WithDisk(Disk("deg-b"));

        await gcp.DeleteDiskAsync("deg-a", Zone, CancellationToken.None);

        gcp.Disks.Select(d => d.Name).ShouldBe(["deg-b"]);
    }

    [Fact]
    public async Task DeleteDisk_of_a_disk_that_is_gone_is_a_success_like_the_real_gateway()
    {
        var gcp = new FakeGcp();

        await Should.NotThrowAsync(() => gcp.DeleteDiskAsync("deg-gone", Zone, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_VM_detaches_its_disks_and_leaves_them_listed()
    {
        var spec = Spec("job1");
        var gcp = new FakeGcp().WithDisk(Disk(spec.VmName, attachedTo: spec.VmName));
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);

        await gcp.DeleteVmAsync(spec.VmName, Zone, CancellationToken.None);

        var disk = gcp.Disks.ShouldHaveSingleItem();
        disk.AttachedTo.ShouldBeEmpty("a deleted VM no longer holds the disk; the disk is what bills on");
    }

    [Fact]
    public async Task A_scripted_delete_failure_hits_only_the_named_disk_and_carries_its_kind()
    {
        var gcp = new FakeGcp().WithDisk(Disk("deg-a")).WithDisk(Disk("deg-b")).WithDeleteFailure("deg-a", CloudErrorKind.Permission);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.DeleteDiskAsync("deg-a", Zone, CancellationToken.None));
        await gcp.DeleteDiskAsync("deg-b", Zone, CancellationToken.None);

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        gcp.Disks.Select(d => d.Name).ShouldBe(["deg-a"]);
    }

    [Fact]
    public async Task Silent_no_op_deletes_report_success_and_remove_nothing()
    {
        var spec = Spec("job1");
        var gcp = new FakeGcp().WithDisk(Disk("deg-a")).WithSilentNoOpDeletes();
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);

        await gcp.DeleteDiskAsync("deg-a", Zone, CancellationToken.None);
        await gcp.DeleteVmAsync(spec.VmName, Zone, CancellationToken.None);

        gcp.Disks.Count.ShouldBe(1);
        (await gcp.GetVmAsync(spec.VmName, Zone, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_disk_listing_fails_when_the_cloud_is_not_connected_rather_than_reading_as_no_disks()
    {
        var gcp = new FakeGcp().WithCloudNotConnected();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.ListDisksByInstallationAsync("install-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(FakeGcp.NotConnectedErrorCode);
    }

    // ---------------------------------------------------------------- prefix usage

    [Fact]
    public async Task GetPrefixUsage_counts_objects_and_bytes_under_the_prefix_only()
    {
        var gcp = new FakeGcp();
        await PutAsync(gcp, "b", "jobs/j1/manifest.json", "12345");
        await PutAsync(gcp, "b", "jobs/j1/output/t.bedgraph", "1234567890");
        await PutAsync(gcp, "b", "jobs/j2/manifest.json", "abc");
        await PutAsync(gcp, "other", "jobs/j1/manifest.json", "zzzzzzzz");

        var usage = await gcp.GetPrefixUsageAsync("b", "jobs/j1/", CancellationToken.None);

        usage.ObjectCount.ShouldBe(2);
        usage.TotalBytes.ShouldBe(15);
    }

    [Fact]
    public async Task GetPrefixUsage_of_an_empty_prefix_is_zero_not_an_error()
    {
        var usage = await new FakeGcp().GetPrefixUsageAsync("b", "jobs/none/", CancellationToken.None);

        usage.ShouldBe(new PrefixUsage(0, 0));
    }

    [Fact]
    public async Task ListChildPrefixes_returns_the_next_path_segment_with_its_slash_once_each()
    {
        var gcp = new FakeGcp();
        await PutAsync(gcp, "b", "jobs/j1/manifest.json", "x");
        await PutAsync(gcp, "b", "jobs/j1/output/a", "x");
        await PutAsync(gcp, "b", "jobs/j2/manifest.json", "x");
        await PutAsync(gcp, "b", "cache/models/m/w", "x");
        await PutAsync(gcp, "b", "app-config.json", "x");

        var children = await gcp.ListChildPrefixesAsync("b", "jobs/", CancellationToken.None);

        children.ShouldBe(["jobs/j1/", "jobs/j2/"]);
    }

    [Fact]
    public async Task DeletePrefix_removes_only_objects_under_that_prefix_and_returns_how_many()
    {
        var gcp = new FakeGcp();
        await PutAsync(gcp, "b", "jobs/j1/manifest.json", "x");
        await PutAsync(gcp, "b", "jobs/j1/output/a", "x");
        await PutAsync(gcp, "b", "jobs/j10/manifest.json", "x");
        await PutAsync(gcp, "b", "cache/models/m/w", "x");

        var deleted = await gcp.DeletePrefixAsync("b", "jobs/j1/", CancellationToken.None);

        deleted.ShouldBe(2);
        gcp.ObjectKeys("b").ShouldBe(["cache/models/m/w", "jobs/j10/manifest.json"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("jobs")]
    public async Task DeletePrefix_refuses_a_prefix_that_is_empty_or_does_not_end_in_a_slash(string prefix)
    {
        var gcp = new FakeGcp();
        await PutAsync(gcp, "b", "jobs/j1/manifest.json", "x");

        await Should.ThrowAsync<ArgumentException>(() => gcp.DeletePrefixAsync("b", prefix, CancellationToken.None));

        gcp.ObjectKeys("b").Count.ShouldBe(1);
    }
}
