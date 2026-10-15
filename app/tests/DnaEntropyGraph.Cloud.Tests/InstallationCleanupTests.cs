using System.Text;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #105: "delete everything for this installation". The two things that must hold are that it scopes by label and
/// manifest (another installation's VM, disk or job data is never touched, nor is anything unlabelled) and that it does not
/// believe a delete: every item is read back, so a delete the platform accepted and did not do is reported, not assumed.
/// </summary>
public class InstallationCleanupTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";
    private const string Mine = "install-1";
    private const string Theirs = "install-2";

    private static VmSpec Spec(string jobId, string installation = Mine) => new(
        ProjectId: Project,
        InstallationId: installation,
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static Dictionary<string, string> Labels(string installation) => new() { ["app"] = VmSpec.AppLabelValue, ["installation-id"] = installation };

    private static DiskDescriptor Disk(string name, string installation = Mine, params string[] attachedTo)
        => new(name, Zone, 150, "READY", Labels(installation), null, attachedTo);

    private static byte[] Manifest(string installation) => Encoding.UTF8.GetBytes("{\"schema\":1,\"createdBy\":{\"installationId\":\"" + installation + "\"}}");

    private static async Task<(FakeGcp Gcp, string Bucket)> RigAsync()
    {
        var gcp = new FakeGcp();
        var bucket = await gcp.EnsureBucketAsync(Project, CancellationToken.None);
        return (gcp, bucket);
    }

    private static void Job(FakeGcp gcp, string bucket, string job, string installation, int outputBytes = 100)
    {
        gcp.PutObject(bucket, $"jobs/{job}/manifest.json", Manifest(installation));
        gcp.PutObject(bucket, $"jobs/{job}/output/track.bedgraph", new byte[outputBytes]);
    }

    private static InstallationCleanup Cleanup(FakeGcp gcp) => new(gcp, gcp);

    // ---------------------------------------------------------------- inventory

    [Fact]
    public async Task Inventory_counts_only_this_installations_vms_disks_and_job_data()
    {
        var (gcp, bucket) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        await gcp.CreateVmAsync(Spec("b", Theirs), Zone, CancellationToken.None);
        gcp.WithDisk(Disk("d-mine")).WithDisk(Disk("d-theirs", Theirs));
        Job(gcp, bucket, "a", Mine, 100);
        Job(gcp, bucket, "z", Theirs, 5000);
        gcp.PutObject(bucket, "cache/models/evo2_7b/w.bin", new byte[7000]);

        var inventory = await Cleanup(gcp).InventoryAsync(Project, Mine, CancellationToken.None);

        inventory.Vms.Select(v => v.Name).ShouldBe(["deg-a"]);
        inventory.Disks.Select(d => d.Name).ShouldBe(["d-mine"]);
        inventory.Bucket.ShouldNotBeNull();
        inventory.Bucket.BucketName.ShouldBe(bucket);
        inventory.Bucket.JobCount.ShouldBe(1);
        inventory.Bucket.ObjectCount.ShouldBe(2);
        inventory.Bucket.TotalBytes.ShouldBe(100 + Manifest(Mine).Length);
        inventory.SharedCache.TotalBytes.ShouldBe(7000, "the model cache is shared by every installation: reported, never part of this installation's data");
    }

    [Fact]
    public async Task Inventory_with_no_bucket_yet_reports_no_bucket_data_instead_of_failing()
    {
        var gcp = new FakeGcp();

        var inventory = await Cleanup(gcp).InventoryAsync(Project, Mine, CancellationToken.None);

        inventory.Bucket.ShouldBeNull();
        inventory.Vms.ShouldBeEmpty();
        inventory.Disks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_prefix_whose_manifest_is_missing_or_unreadable_belongs_to_nobody_and_is_neither_counted_nor_deleted()
    {
        var (gcp, bucket) = await RigAsync();
        gcp.PutObject(bucket, "jobs/orphan/output/t", new byte[10]);
        gcp.PutObject(bucket, "jobs/garbled/manifest.json", Encoding.UTF8.GetBytes("not json {"));
        gcp.PutObject(bucket, "jobs/garbled/output/t", new byte[10]);

        var inventory = await Cleanup(gcp).InventoryAsync(Project, Mine, CancellationToken.None);
        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        inventory.Bucket!.JobCount.ShouldBe(0);
        report.Items.ShouldBeEmpty();
        gcp.ObjectKeys(bucket).Count.ShouldBe(3);
    }

    // ---------------------------------------------------------------- delete all

    [Fact]
    public async Task DeleteAll_removes_this_installations_vms_disks_and_job_data_and_nothing_else()
    {
        var (gcp, bucket) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        await gcp.CreateVmAsync(Spec("b", Theirs), Zone, CancellationToken.None);
        await gcp.CreateVmAsync(Spec("c"), Zone, CancellationToken.None);
        await gcp.StopVmAsync("deg-c", Zone, CancellationToken.None);
        gcp.WithDisk(Disk("d-mine")).WithDisk(Disk("d-theirs", Theirs))
            .WithDisk(new DiskDescriptor("d-unlabelled", Zone, 10, "READY", new Dictionary<string, string>()));
        Job(gcp, bucket, "a", Mine);
        Job(gcp, bucket, "z", Theirs);
        gcp.PutObject(bucket, "cache/models/evo2_7b/w.bin", new byte[1]);
        gcp.PutObject(bucket, "app-config.json", new byte[1]);

        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        report.Complete.ShouldBeTrue(string.Join("; ", report.Items.Select(i => $"{i.Kind} {i.Name} {i.Outcome} {i.Detail}")));
        (await gcp.ListAppVmsAsync(CancellationToken.None)).Select(v => v.Name).ShouldBe(["deg-b"]);
        gcp.Disks.Select(d => d.Name).OrderBy(n => n).ShouldBe(["d-theirs", "d-unlabelled"]);
        gcp.ObjectKeys(bucket).ShouldBe(["app-config.json", "cache/models/evo2_7b/w.bin", "jobs/z/manifest.json", "jobs/z/output/track.bedgraph"]);
        report.Items.Count(i => i.Kind == CleanupKind.Vm && i.Outcome == CleanupOutcome.Deleted).ShouldBe(2);
        report.Items.Count(i => i.Kind == CleanupKind.Disk && i.Outcome == CleanupOutcome.Deleted).ShouldBe(1);
        report.Items.Count(i => i.Kind == CleanupKind.JobData && i.Outcome == CleanupOutcome.Deleted).ShouldBe(1);
    }

    [Fact]
    public async Task A_disk_still_attached_to_one_of_our_VMs_is_deleted_after_the_VM_goes_not_before()
    {
        var (gcp, _) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        gcp.WithDisk(Disk("deg-a", Mine, "deg-a"));

        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        report.Complete.ShouldBeTrue(string.Join("; ", report.Items.Select(i => $"{i.Kind} {i.Name} {i.Outcome} {i.Detail}")));
        gcp.Disks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_labelled_disk_attached_to_a_VM_we_do_not_own_is_skipped_and_left_alone()
    {
        var (gcp, _) = await RigAsync();
        gcp.WithDisk(Disk("d-held", Mine, "someone-elses-vm"));

        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        report.Complete.ShouldBeFalse();
        report.Items.Single().Outcome.ShouldBe(CleanupOutcome.Skipped);
        report.Items.Single().Detail.ShouldNotBeNull().ShouldContain("someone-elses-vm");
        gcp.Disks.Count.ShouldBe(1);
    }

    [Fact]
    public async Task One_failed_delete_is_reported_with_its_error_and_does_not_stop_the_rest()
    {
        var (gcp, bucket) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        gcp.WithDisk(Disk("d-1")).WithDisk(Disk("d-2")).WithDeleteFailure("d-1", CloudErrorKind.Permission);
        Job(gcp, bucket, "a", Mine);

        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        report.Complete.ShouldBeFalse();
        var failed = report.Items.Single(i => i.Outcome == CleanupOutcome.Failed);
        failed.Name.ShouldBe("d-1");
        failed.ErrorKind.ShouldBe(CloudErrorKind.Permission);
        gcp.Disks.Select(d => d.Name).ShouldBe(["d-1"]);
        (await gcp.ListAppVmsAsync(CancellationToken.None)).ShouldBeEmpty();
        gcp.ObjectKeys(bucket).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delete_the_platform_accepted_but_did_not_do_is_reported_as_not_verified_never_as_deleted()
    {
        var (gcp, bucket) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        gcp.WithDisk(Disk("d-1"));
        Job(gcp, bucket, "a", Mine);
        gcp.WithSilentNoOpDeletes();

        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        report.Complete.ShouldBeFalse();
        report.Items.Count.ShouldBe(3);
        report.Items.ShouldAllBe(i => i.Outcome == CleanupOutcome.NotVerified, string.Join("; ", report.Items.Select(i => $"{i.Kind} {i.Outcome}")));
    }

    [Fact]
    public async Task A_listing_that_fails_aborts_the_cleanup_before_any_delete_so_a_blind_list_never_reads_as_nothing_to_do()
    {
        var (gcp, bucket) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        Job(gcp, bucket, "a", Mine);
        gcp.WithCloudNotConnected();

        await Should.ThrowAsync<CloudOperationException>(() => Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None));

        gcp.WithCloudConnected();
        (await gcp.ListAppVmsAsync(CancellationToken.None)).Count.ShouldBe(1);
        gcp.ObjectKeys(bucket).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Not-A-Label")]
    public async Task DeleteAll_refuses_an_installation_id_that_could_match_everything_and_makes_no_call(string installation)
    {
        var compute = Substitute.For<IComputeGateway>();
        var storage = Substitute.For<IStorageGateway>();

        await Should.ThrowAsync<ArgumentException>(() => new InstallationCleanup(compute, storage).DeleteAllAsync(Project, installation, CancellationToken.None));

        compute.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_VM_or_disk_a_gateway_returns_without_our_labels_is_never_deleted_even_if_the_listing_hands_it_over()
    {
        var compute = Substitute.For<IComputeGateway>();
        var storage = Substitute.For<IStorageGateway>();
        compute.ListByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns(
        [
            new VmDescriptor("deg-ours", Zone, "RUNNING", Labels: Labels(Mine)),
            new VmDescriptor("deg-other-installation", Zone, "RUNNING", Labels: Labels(Theirs)),
            new VmDescriptor("deg-no-labels", Zone, "RUNNING"),
            new VmDescriptor("deg-other-app", Zone, "RUNNING", Labels: new Dictionary<string, string> { ["app"] = "x", ["installation-id"] = Mine }),
        ]);
        compute.GetVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((VmDescriptor?)null);
        compute.ListDisksByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns(
        [
            Disk("d-ours"),
            Disk("d-theirs", Theirs),
            new DiskDescriptor("d-none", Zone, 1, "READY", new Dictionary<string, string>()),
        ]);
        storage.ListBucketsAsync(Project, Arg.Any<CancellationToken>()).Returns([]);

        var report = await new InstallationCleanup(compute, storage).DeleteAllAsync(Project, Mine, CancellationToken.None);

        await compute.Received(1).DeleteVmAsync("deg-ours", Zone, Arg.Any<CancellationToken>());
        await compute.Received(1).DeleteVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await compute.Received(1).DeleteDiskAsync("d-ours", Zone, Arg.Any<CancellationToken>());
        await compute.Received(1).DeleteDiskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        report.Items.Count(i => i.Outcome == CleanupOutcome.Skipped).ShouldBe(5);
    }

    [Fact]
    public async Task Cancellation_stops_the_cleanup_and_propagates()
    {
        var (gcp, _) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Cleanup(gcp).DeleteAllAsync(Project, Mine, cts.Token));

        (await gcp.ListAppVmsAsync(CancellationToken.None)).Count.ShouldBe(1);
    }

    // ---------------------------------------------------------------- review findings (rc105)

    private static string Describe(CleanupReport report) => string.Join("; ", report.Items.Select(i => $"{i.Kind} {i.Name} {i.Outcome} {i.Detail}"));

    [Fact]
    public async Task A_failing_read_back_of_a_VM_is_reported_and_the_disks_and_job_data_are_still_deleted()
    {
        var (gcp, bucket) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a"), Zone, CancellationToken.None);
        gcp.WithDisk(Disk("d-1"));
        Job(gcp, bucket, "a", Mine);
        gcp.WithGetVmFailure(CloudErrorKind.Network);

        var report = await new InstallationCleanup(gcp, gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        var vm = report.Items.Single(i => i.Kind == CleanupKind.Vm);
        vm.Outcome.ShouldBe(CleanupOutcome.NotVerified, Describe(report));
        vm.ErrorKind.ShouldBe(CloudErrorKind.Network);
        gcp.Disks.ShouldBeEmpty("the disk delete after the failed read back must still run");
        gcp.ObjectKeys(bucket).ShouldBeEmpty("the job data delete after the failed read back must still run");
    }

    [Fact]
    public async Task A_failing_second_disk_listing_or_prefix_read_back_is_reported_per_item_and_returns_a_report()
    {
        var (gcp, bucket) = await RigAsync();
        gcp.WithDisk(Disk("d-1"));
        Job(gcp, bucket, "a", Mine);
        var compute = Substitute.For<IComputeGateway>();
        var storage = Substitute.For<IStorageGateway>();
        var disk = Disk("d-1");
        var listCalls = 0;
        compute.ListByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns([]);
        compute.ListDisksByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // 1: the up-front listing, 2: the one after the VMs, 3: the read back after the disk delete (fails).
            listCalls++;
            return listCalls >= 3 ? throw new CloudOperationException(new CloudError("X", 503, "boom"), CloudErrorKind.Network) : Task.FromResult<IReadOnlyList<DiskDescriptor>>([disk]);
        });
        storage.ListBucketsAsync(Project, Arg.Any<CancellationToken>()).Returns([new BucketDescriptor("b", "US", new Dictionary<string, string>(), null)]);
        storage.ListChildPrefixesAsync("b", ResultsBucket.JobsPrefix, Arg.Any<CancellationToken>()).Returns(["jobs/a/"]);
        storage.TryDownloadAsync("b", "jobs/a/manifest.json", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(Manifest(Mine))));
        storage.GetPrefixUsageAsync("b", "jobs/a/", Arg.Any<CancellationToken>()).Returns<Task<PrefixUsage>>(_ => throw new CloudOperationException(new CloudError("X", 503, "boom"), CloudErrorKind.Network));

        var report = await new InstallationCleanup(compute, storage).DeleteAllAsync(Project, Mine, CancellationToken.None);

        report.Items.Single(i => i.Kind == CleanupKind.Disk).Outcome.ShouldBe(CleanupOutcome.NotVerified, Describe(report));
        report.Items.Single(i => i.Kind == CleanupKind.JobData).Outcome.ShouldBe(CleanupOutcome.NotVerified, Describe(report));
        await storage.Received(1).DeletePrefixAsync("b", "jobs/a/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_job_folder_whose_key_walks_out_of_jobs_is_reported_and_does_not_abort_the_sweep()
    {
        var (gcp, bucket) = await RigAsync();
        gcp.PutObject(bucket, "jobs/../manifest.json", Manifest(Mine));
        gcp.PutObject(bucket, "jobs/../output/t", new byte[5]);
        Job(gcp, bucket, "good", Mine);

        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        var forged = report.Items.Single(i => i.Name == "jobs/../");
        forged.Outcome.ShouldBe(CleanupOutcome.Failed, Describe(report));
        forged.Detail.ShouldBe(InstallationCleanup.PrefixRefusedCode);
        report.Items.Single(i => i.Name == "jobs/good/").Outcome.ShouldBe(CleanupOutcome.Deleted);
        gcp.ObjectKeys(bucket).ShouldBe(["jobs/../manifest.json", "jobs/../output/t"], "the refused prefix is left alone");
    }

    [Fact]
    public async Task An_installation_id_that_is_a_prefix_of_another_matches_only_itself()
    {
        var (gcp, bucket) = await RigAsync();
        await gcp.CreateVmAsync(Spec("a", "ab"), Zone, CancellationToken.None);
        await gcp.CreateVmAsync(Spec("b", "abc"), Zone, CancellationToken.None);
        gcp.WithDisk(Disk("d-ab", "ab")).WithDisk(Disk("d-abc", "abc"));
        Job(gcp, bucket, "ja", "ab");
        Job(gcp, bucket, "jb", "abc");

        var report = await Cleanup(gcp).DeleteAllAsync(Project, "ab", CancellationToken.None);

        report.Complete.ShouldBeTrue(Describe(report));
        (await gcp.ListAppVmsAsync(CancellationToken.None)).Select(v => v.Name).ShouldBe(["deg-b"]);
        gcp.Disks.Select(d => d.Name).ShouldBe(["d-abc"]);
        gcp.ObjectKeys(bucket).ShouldAllBe(k => k.StartsWith("jobs/jb/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancel_in_the_middle_of_the_sweep_stops_it_and_deletes_nothing_more()
    {
        var (gcp, bucket) = await RigAsync();
        Job(gcp, bucket, "a", Mine);
        Job(gcp, bucket, "b", Mine);
        using var cts = new CancellationTokenSource();
        gcp.AfterPrefixDelete(_ => cts.Cancel());

        await Should.ThrowAsync<OperationCanceledException>(() => new InstallationCleanup(gcp, gcp).DeleteAllAsync(Project, Mine, cts.Token));

        gcp.ObjectKeys(bucket).ShouldBe(["jobs/b/manifest.json", "jobs/b/output/track.bedgraph"]);
    }

    [Fact]
    public async Task Job_data_in_a_bucket_with_soft_delete_is_reported_deleted_with_the_days_Google_keeps_a_recoverable_copy()
    {
        var (gcp, bucket) = await RigAsync();
        Job(gcp, bucket, "a", Mine);
        gcp.WithBucketSoftDeleteDays(7);

        var report = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        var item = report.Items.Single();
        item.Outcome.ShouldBe(CleanupOutcome.Deleted);
        item.RecoverableForDays.ShouldBe(7);
    }

    [Fact]
    public async Task Job_data_in_a_bucket_with_soft_delete_off_or_unknown_carries_no_recoverable_days()
    {
        var (gcp, bucket) = await RigAsync();
        Job(gcp, bucket, "a", Mine);

        var unknown = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);
        Job(gcp, bucket, "b", Mine);
        gcp.WithBucketSoftDeleteDays(0);
        var off = await Cleanup(gcp).DeleteAllAsync(Project, Mine, CancellationToken.None);

        unknown.Items.Single().RecoverableForDays.ShouldBeNull();
        off.Items.Single().RecoverableForDays.ShouldBeNull();
    }

    // ---------------------------------------------------------------- run rows (Hard Rule 11)

    private sealed class MemoryRuns : DnaEntropyGraph.Core.Abstractions.IRunRepository
    {
        public List<DnaEntropyGraph.Core.Abstractions.RunRecord> Rows { get; } = [];

        public Task<IReadOnlyList<DnaEntropyGraph.Core.Abstractions.RunRecord>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DnaEntropyGraph.Core.Abstractions.RunRecord>>(Rows.ToList());

        public Task UpsertAsync(DnaEntropyGraph.Core.Abstractions.RunRecord run, CancellationToken cancellationToken)
        {
            Rows.RemoveAll(r => r.JobId == run.JobId);
            Rows.Add(run);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Deleting_the_VM_of_a_running_job_marks_its_row_Cancelled_with_a_named_code_and_stops_its_driver_first()
    {
        var (gcp, _) = await RigAsync();
        await gcp.CreateVmAsync(Spec("run-1"), Zone, CancellationToken.None);
        await gcp.CreateVmAsync(Spec("done-1"), Zone, CancellationToken.None);
        var runs = new MemoryRuns();
        runs.Rows.Add(new DnaEntropyGraph.Core.Abstractions.RunRecord("run-1", DnaEntropyGraph.Core.JobPhase.Running, DateTimeOffset.UtcNow));
        runs.Rows.Add(new DnaEntropyGraph.Core.Abstractions.RunRecord("done-1", DnaEntropyGraph.Core.JobPhase.Completed, DateTimeOffset.UtcNow));
        var active = new ActiveRuns();
        var stopped = new TaskCompletionSource();
        var driver = active.TryStart("run-1", async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                stopped.SetResult();
            }
        });
        var phases = new List<(string, DnaEntropyGraph.Core.JobPhase)>();

        await new InstallationCleanup(gcp, gcp, runs, active, (id, phase) => phases.Add((id, phase))).DeleteAllAsync(Project, Mine, CancellationToken.None);

        stopped.Task.IsCompleted.ShouldBeTrue("the driver is stopped so it cannot write over the row");
        var row = runs.Rows.Single(r => r.JobId == "run-1");
        row.Phase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Cancelled);
        row.ErrorCode.ShouldBe(RunErrorCodes.DeletedByCleanup);
        row.FinishedAt.ShouldNotBeNull();
        runs.Rows.Single(r => r.JobId == "done-1").Phase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Completed);
        phases.ShouldBe([("run-1", DnaEntropyGraph.Core.JobPhase.Cancelling), ("run-1", DnaEntropyGraph.Core.JobPhase.Cancelled)]);
        RunErrorCodes.All.ShouldContain(RunErrorCodes.DeletedByCleanup);
        RunErrorCodes.ResourceKey(RunErrorCodes.DeletedByCleanup).ShouldNotBe("RunError_other");
    }

    [Fact]
    public async Task A_row_is_not_marked_when_its_VM_delete_failed_because_the_run_may_still_be_going()
    {
        var (gcp, _) = await RigAsync();
        await gcp.CreateVmAsync(Spec("run-1"), Zone, CancellationToken.None);
        gcp.WithDeleteFailure("deg-run-1", CloudErrorKind.Permission);
        var runs = new MemoryRuns();
        runs.Rows.Add(new DnaEntropyGraph.Core.Abstractions.RunRecord("run-1", DnaEntropyGraph.Core.JobPhase.Running, DateTimeOffset.UtcNow));

        await new InstallationCleanup(gcp, gcp, runs, new ActiveRuns()).DeleteAllAsync(Project, Mine, CancellationToken.None);

        runs.Rows.Single().Phase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Running);
    }
}