using System.Collections.Concurrent;
using System.Reflection;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

// #918: every piece of per-VM state in FakeGcp belongs to one VM incarnation (InstanceId), never to a name that a delete-and-recreate hands to the next VM.
public class FakeGcpIncarnationTests
{
    private const string Zone = "us-central1-a";

    private static byte[] Manifest => """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray();

    private static VmSpec Spec(string jobId, TimeSpan? maxRun = null) => new(
        ProjectId: "fake-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: maxRun ?? TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    [Fact]
    public async Task A_delete_does_not_wipe_the_label_fingerprint_of_a_vm_made_again_under_the_name_in_the_gap()
    {
        var gcp = new FakeGcp();
        var first = await gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None);
        VmDescriptor? second = null;
        gcp.BeforePlanCleanup = () =>
        {
            gcp.BeforePlanCleanup = null;
            second = gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
        };

        await gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None);

        second.ShouldNotBeNull("vacuity: the VM was made again inside the delete's gap");
        second.InstanceId.ShouldNotBe(first.InstanceId);
        gcp.LabelFingerprintOf(second.Name, second.Zone).ShouldBeGreaterThan(0L);
    }

    [Fact]
    public async Task A_vm_made_again_under_a_name_does_not_inherit_the_deleted_vms_max_run_duration()
    {
        var gcp = new FakeGcp();
        var first = await gcp.CreateVmAsync(Spec("job-1", TimeSpan.FromHours(1)), Zone, CancellationToken.None);
        gcp.MaxRunDurationOf(first.Name, Zone).ShouldBe(TimeSpan.FromHours(1)); // vacuity
        await gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None);

        gcp.WithAlreadyExists(first.Name, Zone); // a different VM now holds the name, one this fake never gave a limit

        gcp.MaxRunDurationOf(first.Name, Zone).ShouldBeNull();
    }

    [Fact]
    public async Task A_boot_poll_that_loses_a_race_to_a_delete_and_recreate_does_not_finish_the_new_vms_boot()
    {
        var gcp = new FakeGcp().WithBootStatusPolls(1);
        var first = await gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None);
        (await gcp.GetVmAsync(first.Name, Zone, CancellationToken.None))!.Status.ShouldBe("PROVISIONING"); // uses the one poll
        VmDescriptor? second = null;
        gcp.BeforePollWrite = () =>
        {
            gcp.BeforePollWrite = null;
            gcp.DeleteVmAsync(first.Name, Zone, CancellationToken.None).GetAwaiter().GetResult();
            second = gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
        };

        var polled = await gcp.GetVmAsync(first.Name, Zone, CancellationToken.None);

        second.ShouldNotBeNull("vacuity: the poll's write was interleaved with a delete and a create");
        polled!.InstanceId.ShouldBe(second.InstanceId);
        polled.Status.ShouldBe("PROVISIONING");
    }

    [Fact]
    public async Task A_stopping_poll_that_loses_a_race_to_a_delete_and_recreate_does_not_finish_the_new_vms_stop()
    {
        var gcp = new FakeGcp().WithStopBehaviour(stoppingPolls: 1);
        var first = await gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(first.Name, Zone, CancellationToken.None);
        (await gcp.GetVmAsync(first.Name, Zone, CancellationToken.None))!.Status.ShouldBe("STOPPING"); // uses the one poll
        VmDescriptor? second = null;
        gcp.BeforePollWrite = () =>
        {
            gcp.BeforePollWrite = null;
            gcp.DeleteVmAsync(first.Name, Zone, CancellationToken.None).GetAwaiter().GetResult();
            second = gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
            gcp.StopVmAsync(first.Name, Zone, CancellationToken.None).GetAwaiter().GetResult();
        };

        var polled = await gcp.GetVmAsync(first.Name, Zone, CancellationToken.None);

        second.ShouldNotBeNull("vacuity: the poll's write was interleaved with a delete, a create and a stop");
        polled!.InstanceId.ShouldBe(second.InstanceId);
        polled.Status.ShouldBe("STOPPING");
    }

    [Fact]
    public async Task An_internal_stop_in_flight_does_not_excuse_an_outside_stop_of_a_vm_that_is_not_running()
    {
        var gcp = new FakeGcp().WithStopRejectedUnlessRunning().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Stop);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-1/manifest.json", Manifest);
        var worker = await gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None); // its worker queued a self-stop for when result.json is read
        var other = await gcp.CreateVmAsync(Spec("job-2"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(other.Name, Zone, CancellationToken.None);
        CloudOperationException? rejected = null;
        var fired = false;
        gcp.BeforeStopWrite = () =>
        {
            gcp.BeforeStopWrite = null;
            fired = true;
            try
            {
                gcp.StopVmAsync(other.Name, Zone, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (CloudOperationException ex)
            {
                rejected = ex;
            }
        };

        await gcp.TryDownloadAsync(bucket, "jobs/job-1/result.json", CancellationToken.None); // the worker's self-stop runs now, as an internal stop

        fired.ShouldBeTrue("vacuity: the internal stop reached its write");
        rejected.ShouldNotBeNull("an outside stop of a STOPPED VM is rejected, whatever internal stop happens to be running");
        rejected.Error.Code.ShouldBe("FAILED_PRECONDITION");
        (await gcp.GetVmAsync(worker.Name, Zone, CancellationToken.None))!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task Two_creates_of_one_vm_that_overlap_between_the_exists_check_and_the_insert_make_one_vm()
    {
        var gcp = new FakeGcp();
        VmDescriptor? inner = null;
        gcp.BeforeGrantWrite = () =>
        {
            gcp.BeforeGrantWrite = null;
            inner = gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
        };

        var outer = await gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None);

        inner.ShouldNotBeNull("vacuity: a second create ran between the first one's check and its insert");
        outer.InstanceId.ShouldBe(inner.InstanceId); // the replay of one request, not a second VM that replaced the first
        (await gcp.GetVmAsync(outer.Name, Zone, CancellationToken.None))!.InstanceId.ShouldBe(inner.InstanceId);
    }

    [Fact]
    public void The_quota_and_already_exists_tables_are_concurrent_because_a_quota_grant_writes_them_while_a_read_enumerates_them()
    {
        // #918: setup-only was not true: FakeGcp.QuotaRequest.cs writes _regionalQuota at run time, while the quota read enumerates it.
        foreach (var name in new[] { "_regionalQuota", "_regionalUsed", "_alreadyExistingVmKeys" })
        {
            var field = typeof(FakeGcp).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            field.ShouldNotBeNull(name);
            field.FieldType.IsGenericType.ShouldBeTrue(name);
            field.FieldType.GetGenericTypeDefinition().ShouldBe(typeof(ConcurrentDictionary<,>), name);
        }
    }

    [Fact]
    public async Task A_failed_reuse_does_not_replace_a_key_another_actor_overwrote_after_the_worker_wrote_it()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", Manifest);
        var created = await gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, Zone, CancellationToken.None);
        var workerHadWritten = false;
        gcp.BeforeStartWrite = () =>
        {
            workerHadWritten = gcp.ObjectKeys(bucket).Contains("jobs/job-2/result.json");
            gcp.PutObject(bucket, "jobs/job-2/result.json", "from-another-actor"u8.ToArray()); // a key the worker wrote, rewritten by someone else
            gcp.DeleteVmAsync(created.Name, Zone, CancellationToken.None).GetAwaiter().GetResult();
        };

        await Should.ThrowAsync<CloudOperationException>(() => gcp.ReuseVmAsync(Spec("job-2"), created.Name, Zone, CancellationToken.None));

        workerHadWritten.ShouldBeTrue("vacuity: the worker had written result.json before the other actor overwrote it");
        using var stream = (await gcp.TryDownloadAsync(bucket, "jobs/job-2/result.json", CancellationToken.None))!;
        new StreamReader(stream).ReadToEnd().ShouldBe("from-another-actor");
    }

    [Fact]
    public async Task A_reuse_whose_start_write_throws_takes_the_workers_files_and_pending_end_back()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", Manifest);
        var created = await gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, Zone, CancellationToken.None);
        gcp.BeforeStartWrite = () => throw new InvalidOperationException("the start write blew up");

        await Should.ThrowAsync<InvalidOperationException>(() => gcp.ReuseVmAsync(Spec("job-2"), created.Name, Zone, CancellationToken.None));

        gcp.ObjectKeys(bucket).Where(k => k.StartsWith("jobs/job-2/", StringComparison.Ordinal)).ShouldBe(["jobs/job-2/manifest.json"]);
        gcp.PendingWorkerEndCount.ShouldBe(0);
        (await gcp.GetVmAsync(created.Name, Zone, CancellationToken.None))!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_create_whose_grant_write_throws_takes_the_workers_files_and_pending_end_back()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-1/manifest.json", Manifest);
        gcp.BeforeGrantWrite = () => throw new InvalidOperationException("the grant write blew up");

        await Should.ThrowAsync<InvalidOperationException>(() => gcp.CreateVmAsync(Spec("job-1"), Zone, CancellationToken.None));

        gcp.ObjectKeys(bucket).Where(k => k.StartsWith("jobs/job-1/", StringComparison.Ordinal)).ShouldBe(["jobs/job-1/manifest.json"]);
        gcp.PendingWorkerEndCount.ShouldBe(0);
    }
}
