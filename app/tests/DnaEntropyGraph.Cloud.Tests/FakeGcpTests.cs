using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

public class FakeGcpTests
{
    private const string Zone = "us-central1-a";

    private static VmSpec ValidSpec(string jobId) => new(
        ProjectId: "fake-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    [Fact]
    public async Task A_delete_that_lands_between_a_stops_read_and_write_leaves_the_vm_deleted_when_the_stop_takes_polls()
    {
        // The STOPPING branch of the same race (#875).
        var gcp = new FakeGcp().WithStopBehaviour(stoppingPolls: 2);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        gcp.BeforeStopWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);

        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_delete_that_lands_between_a_reuses_claim_and_its_start_leaves_the_vm_deleted_and_the_reuse_says_gone()
    {
        // Start vs Delete (#875): real Compute answers a start of a deleted instance with a 404; the VM must not come back RUNNING.
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        gcp.BeforeStartWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.ReuseVmAsync(ValidSpec("job-2"), created.Name, created.Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe("VM_NOT_FOUND");
        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None)).ShouldBeNull();
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;

    [Fact]
    public void Only_a_marked_create_may_assign_a_vm_row_directly_every_change_goes_through_UpdateVm()
    {
        // #875: a bare `_vms[key] = ...` (or AddOrUpdate/GetOrAdd/TryAdd/TryUpdate) is a read-modify-write that can resurrect a deleted VM. FakeGcp is a partial class: every FakeGcp*.cs
        // is scanned and an assignment may span lines. An assignment is allowed only right after a `vm-row-create` marker AND only when it inserts a new row; the compare-and-swap
        // calls are allowed only inside UpdateVm itself. Removals (a delete) are fine anywhere.
        var dir = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "src", "DnaEntropyGraph.Cloud");
        var files = Directory.GetFiles(dir, "FakeGcp*.cs");
        files.Length.ShouldBeGreaterThan(3); // vacuity: the partial files were found
        var assignment = new System.Text.RegularExpressions.Regex(@"_vms\s*\[(?:[^\[\]]|\[[^\[\]]*\])*\]\s*=(?<rhs>[^=][^;]*)");
        var writer = new System.Text.RegularExpressions.Regex(@"_vms\s*\.\s*(AddOrUpdate|GetOrAdd|TryAdd|TryUpdate)\b");
        var unmarked = new List<string>();
        var writesOutsideHelper = new List<string>();
        var updateVmCalls = 0;
        var marked = 0;
        var markers = 0;
        var helperWrites = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            updateVmCalls += System.Text.RegularExpressions.Regex.Matches(text, @"\bUpdateVm\(").Count;
            // Comments blanked to spaces of the same length, so a match's index still addresses the original text (and the marker comment).
            var code = System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", m => new string(' ', m.Length), System.Text.RegularExpressions.RegexOptions.Singleline);
            markers += System.Text.RegularExpressions.Regex.Matches(text, "vm-row-create").Count;

            // The body of UpdateVm, by brace counting on the comment-free text.
            var helperStart = code.IndexOf("VmDescriptor? UpdateVm(", StringComparison.Ordinal);
            var helperEnd = -1;
            if (helperStart >= 0)
            {
                var open = code.IndexOf('{', helperStart);
                var depth = 0;
                for (var i = open; i < code.Length; i++)
                {
                    depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
                    if (depth == 0)
                    {
                        helperEnd = i;
                        break;
                    }
                }
            }

            foreach (System.Text.RegularExpressions.Match m in writer.Matches(code))
            {
                if (helperStart >= 0 && m.Index > helperStart && m.Index < helperEnd)
                {
                    helperWrites++;
                }
                else
                {
                    writesOutsideHelper.Add(Path.GetFileName(file) + ": " + m.Value);
                }
            }

            foreach (System.Text.RegularExpressions.Match m in assignment.Matches(code))
            {
                var before = text[Math.Max(0, m.Index - 300)..m.Index];
                var at = before.LastIndexOf("vm-row-create", StringComparison.Ordinal);
                var rhs = m.Groups["rhs"].Value.TrimStart();
                var insertsNewRow = rhs.StartsWith("new VmDescriptor(", StringComparison.Ordinal) || rhs.Trim() == "vm";
                if (at >= 0 && !before[at..].Contains(';', StringComparison.Ordinal) && insertsNewRow)
                {
                    marked++;
                }
                else
                {
                    unmarked.Add(Path.GetFileName(file) + ": " + m.Value);
                }
            }
        }

        unmarked.ShouldBeEmpty();
        writesOutsideHelper.ShouldBeEmpty();
        marked.ShouldBeGreaterThan(0); // vacuity: the creates were found
        markers.ShouldBe(marked); // every marker sits on a create, and no marker is left on something else
        helperWrites.ShouldBeGreaterThan(0); // vacuity: the helper's own swap was found
        updateVmCalls.ShouldBeGreaterThan(5); // vacuity: the helper is really used
    }
    [Fact]
    public async Task A_delete_that_lands_before_a_plain_start_writes_leaves_the_vm_deleted_and_the_start_says_gone()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        gcp.BeforeStartWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.StartVmAsync(created.Name, created.Zone, CancellationToken.None));

        ex.Error.Code.ShouldBe("VM_NOT_FOUND");
        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_preempt_that_lands_between_a_stops_read_and_write_is_not_overwritten_with_stopped()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        gcp.WithPreemption(created.Name, created.Zone, TimeSpan.Zero);
        gcp.BeforeStopWrite = () => gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult(); // the poll applies the preempt

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);

        var vm = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);
        vm.ShouldNotBeNull();
        vm.Status.ShouldBe("TERMINATED");
        vm.StatusReason.ShouldBe("preempted");
    }

    [Fact]
    public async Task A_delete_that_lands_between_a_stops_read_and_write_leaves_the_vm_deleted_like_real_compute()
    {
        // Issue #875: a stop is a read-modify-write of the VM row. A delete interleaved there used to be overwritten, resurrecting a deleted VM as STOPPED.
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        gcp.BeforeStopWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);

        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None)).ShouldBeNull();
    }

    private static VmSpec SpotSpec(string jobId) => ValidSpec(jobId) with { Spot = true };

    private static VmSpec FlexSpec(string jobId) => ValidSpec(jobId) with { FlexStart = true, FlexStartWait = TimeSpan.FromHours(2) };

    private static Task<bool> WasPreempted(FakeGcp gcp, VmDescriptor vm)
        => gcp.WasPreemptedAsync(vm.Name, vm.Zone, new VmIncarnation(vm.InstanceId, vm.CreatedAt), CancellationToken.None);

    // ---- cold review: M1 (a stop is never silently dropped by a state change) ----

    [Fact]
    public async Task A_stop_of_a_booting_vm_that_a_poll_moves_to_running_before_the_write_still_stops_it()
    {
        var gcp = new FakeGcp().WithBootStatusPolls(1);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        created.Status.ShouldBe("PROVISIONING");
        gcp.BeforeStopWrite = () =>
        {
            gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();
            gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult(); // PROVISIONING -> RUNNING
        };

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);

        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None))!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task A_second_stop_whose_stopping_vm_finishes_stopping_before_the_write_does_not_re_enter_stopping()
    {
        var gcp = new FakeGcp().WithStopBehaviour(stoppingPolls: 1);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None); // STOPPING
        gcp.BeforeStopWrite = () =>
        {
            gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();
            gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult(); // STOPPING -> STOPPED
        };

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);

        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None))!.Status.ShouldBe("STOPPED");
    }

    // ---- cold review: M2 (the polls' own compare-and-swaps) ----

    [Fact]
    public async Task A_delete_that_lands_during_the_boot_poll_leaves_the_vm_deleted()
    {
        var gcp = new FakeGcp().WithBootStatusPolls(1);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None); // the boot countdown
        gcp.BeforePollWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        var seen = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        seen.ShouldBeNull();
        gcp.BeforePollWrite = null;
        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_delete_that_lands_during_the_stopping_poll_leaves_the_vm_deleted()
    {
        var gcp = new FakeGcp().WithStopBehaviour(stoppingPolls: 1);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None); // the stopping countdown
        gcp.BeforePollWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        var seen = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        seen.ShouldBeNull();
        gcp.BeforePollWrite = null;
        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_delete_that_lands_during_the_preempt_poll_leaves_the_vm_deleted_and_records_no_preemption()
    {
        var gcp = new FakeGcp().WithSpotPreemptions(1);
        var created = await gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None);
        gcp.BeforePollWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        var seen = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        seen.ShouldBeNull();
        gcp.PreemptionRecordCount.ShouldBe(0);
        (await WasPreempted(gcp, created)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_spot_preempt_that_lands_between_a_stops_read_and_write_is_kept_and_recorded_exactly_once()
    {
        var gcp = new FakeGcp().WithSpotPreemptions(1);
        var created = await gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None);
        gcp.BeforeStopWrite = () => gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult(); // the poll applies the preempt

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);

        var vm = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);
        vm!.Status.ShouldBe("TERMINATED");
        vm.StatusReason.ShouldBe("preempted");
        gcp.PreemptionRecordCount.ShouldBe(1);
        (await WasPreempted(gcp, created)).ShouldBeTrue();
    }

    // ---- cold review: L3 (a delete takes the scheduled preemption with it) ----

    [Fact]
    public async Task A_vm_made_again_under_the_name_of_a_deleted_one_inherits_no_scheduled_preemption()
    {
        var gcp = new FakeGcp().WithSpotPreemptions(1);
        var first = await gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None); // a preemption is scheduled for it
        await gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None); // deleted before any poll applied it
        var second = await gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None);

        var seen = await gcp.GetVmAsync(second.Name, second.Zone, CancellationToken.None);

        seen!.Status.ShouldBe("RUNNING");
        gcp.PreemptionRecordCount.ShouldBe(0);
    }

    // ---- cold review: L4 (relabel merges the labels as they are NOW) ----

    [Fact]
    public async Task An_external_relabel_merges_into_the_labels_and_keeps_the_others()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);

        gcp.SimulateExternalRelabel(created.Name, created.Zone, new Dictionary<string, string> { ["extra"] = "yes" });

        var vm = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);
        vm!.Labels!["extra"].ShouldBe("yes");
        vm.Labels["job-id"].ShouldBe(created.Labels!["job-id"]);
    }

    // ---- cold review: L5 (a reuse deleted mid-flight starts no worker) ----

    [Fact]
    public async Task A_reuse_deleted_mid_flight_starts_no_worker_so_no_stale_worker_end_can_delete_a_later_vm()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray());
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        gcp.BeforeStartWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();
        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.ReuseVmAsync(ValidSpec("job-2"), created.Name, created.Zone, CancellationToken.None));
        ex.Error.Code.ShouldBe("VM_NOT_FOUND");
        gcp.BeforeStartWrite = null;
        var later = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None); // a different VM under the same name

        (await gcp.TryDownloadAsync(bucket, "jobs/job-2/result.json", CancellationToken.None)).ShouldBeNull();

        (await gcp.GetVmAsync(later.Name, later.Zone, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_worker_end_of_an_earlier_vm_never_removes_a_later_vm_under_the_same_name()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-1/manifest.json", """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray());
        var first = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None); // its worker will delete it when the result is read
        await gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None);
        gcp.WithWorker(FakeWorkerMode.Never); // the later VM has no worker of its own, so the earlier one's deferred end is the only one pending
        var later = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);

        await gcp.TryDownloadAsync(bucket, "jobs/job-1/result.json", CancellationToken.None);

        (await gcp.GetVmAsync(later.Name, later.Zone, CancellationToken.None)).ShouldNotBeNull();
    }

    // ---- cold review: L7 / #910 (a queued request becomes the granted VM in one step) ----

    [Fact]
    public async Task A_poll_while_a_queued_request_is_being_granted_never_sees_the_vm_missing()
    {
        var gcp = new FakeGcp().WithFlexStartHeld();
        var create = gcp.CreateVmAsync(FlexSpec("job-1"), Zone, CancellationToken.None);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var name = FlexSpec("job-1").VmName;
        VmDescriptor? polledInTheGap = null;
        gcp.BeforeGrantWrite = () => polledInTheGap = gcp.GetVmAsync(name, Zone, CancellationToken.None).GetAwaiter().GetResult();

        gcp.GrantFlexStart();
        var granted = await create.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        polledInTheGap.ShouldNotBeNull("the request exists, queued, until the granted VM replaces it");
        (await gcp.GetVmAsync(name, Zone, CancellationToken.None))!.InstanceId.ShouldBe(granted.InstanceId);
    }

    [Fact]
    public async Task A_delete_that_lands_while_a_queued_request_is_being_granted_stays_deleted_and_the_create_says_cancelled()
    {
        var gcp = new FakeGcp().WithFlexStartHeld();
        var create = gcp.CreateVmAsync(FlexSpec("job-1"), Zone, CancellationToken.None);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var name = FlexSpec("job-1").VmName;
        gcp.BeforeGrantWrite = () => gcp.DeleteVmAsync(name, Zone, CancellationToken.None).GetAwaiter().GetResult();

        gcp.GrantFlexStart();
        var ex = await Should.ThrowAsync<CloudOperationException>(() => create.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        ex.Error.Code.ShouldBe("FLEX_START_REQUEST_CANCELLED");
        gcp.BeforeGrantWrite = null;
        (await gcp.GetVmAsync(name, Zone, CancellationToken.None)).ShouldBeNull();
    }
    // ---- cold review round 2 ----

    [Fact]
    public async Task A_vm_made_again_between_a_deletes_removal_and_its_cleanup_keeps_its_own_scheduled_preemption()
    {
        var gcp = new FakeGcp().WithSpotPreemptions(2);
        var first = await gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None);
        VmDescriptor? second = null;
        gcp.BeforePlanCleanup = () => second = gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
        await gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None);
        gcp.BeforePlanCleanup = null;

        var seen = await gcp.GetVmAsync(second!.Name, second.Zone, CancellationToken.None);

        seen!.Status.ShouldBe("TERMINATED", "the second VM's own preemption was not wiped by the first one's cleanup");
        (await WasPreempted(gcp, second)).ShouldBeTrue();
        (await WasPreempted(gcp, first)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_vm_made_again_between_a_polls_failed_write_and_its_cleanup_keeps_its_own_scheduled_preemption()
    {
        var gcp = new FakeGcp().WithSpotPreemptions(2);
        var first = await gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None);
        VmDescriptor? second = null;
        gcp.BeforePollWrite = () =>
        {
            gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None).GetAwaiter().GetResult();
            gcp.BeforePlanCleanup = () => second = gcp.CreateVmAsync(SpotSpec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
        };
        (await gcp.GetVmAsync(first.Name, first.Zone, CancellationToken.None)).ShouldBeNull();
        gcp.BeforePollWrite = null;
        gcp.BeforePlanCleanup = null;

        var seen = await gcp.GetVmAsync(second!.Name, second.Zone, CancellationToken.None);

        seen!.Status.ShouldBe("TERMINATED");
        (await WasPreempted(gcp, second)).ShouldBeTrue();
    }

    private static byte[] Manifest => """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray();

    [Fact]
    public async Task A_reuse_has_the_workers_files_in_the_bucket_before_the_vm_reads_running()
    {
        var gcp = new FakeGcp();
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", Manifest);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        var filesBeforeStart = false;
        var statusBeforeStart = "";
        gcp.BeforeStartWrite = () =>
        {
            filesBeforeStart = gcp.ObjectKeys(bucket).Contains("jobs/job-2/result.json");
            statusBeforeStart = gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult()!.Status;
        };

        await gcp.ReuseVmAsync(ValidSpec("job-2"), created.Name, created.Zone, CancellationToken.None);

        statusBeforeStart.ShouldBe("STOPPED");
        filesBeforeStart.ShouldBeTrue("files first, then the VM becomes visible as RUNNING (#784)");
    }

    [Fact]
    public async Task A_reuse_whose_worker_cannot_read_the_manifest_leaves_the_vm_stopped_and_no_worker_files()
    {
        var gcp = new FakeGcp();
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", "this is not json"u8.ToArray());
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);

        await Should.ThrowAsync<Exception>(() => gcp.ReuseVmAsync(ValidSpec("job-2"), created.Name, created.Zone, CancellationToken.None));

        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None))!.Status.ShouldBe("STOPPED");
        gcp.ObjectKeys(bucket).ShouldNotContain("jobs/job-2/result.json");
    }

    [Fact]
    public async Task A_reuse_deleted_mid_flight_leaves_no_worker_objects_and_no_pending_end()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", Manifest);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        gcp.BeforeStartWrite = () => gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();

        await Should.ThrowAsync<CloudOperationException>(() => gcp.ReuseVmAsync(ValidSpec("job-2"), created.Name, created.Zone, CancellationToken.None));

        gcp.ObjectKeys(bucket).Where(k => k.StartsWith("jobs/job-2/", StringComparison.Ordinal)).ShouldBe(["jobs/job-2/manifest.json"]);
        gcp.PendingWorkerEndCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_flex_start_grant_cancelled_by_a_delete_leaves_no_worker_objects_for_the_job()
    {
        var gcp = new FakeGcp().WithFlexStartHeld();
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-1/manifest.json", Manifest);
        var create = gcp.CreateVmAsync(FlexSpec("job-1"), Zone, CancellationToken.None);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var name = FlexSpec("job-1").VmName;
        gcp.BeforeGrantWrite = () => gcp.DeleteVmAsync(name, Zone, CancellationToken.None).GetAwaiter().GetResult();
        gcp.GrantFlexStart();

        await Should.ThrowAsync<CloudOperationException>(() => create.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        (await gcp.TryDownloadAsync(bucket, "jobs/job-1/result.json", CancellationToken.None)).ShouldBeNull();
        gcp.ObjectKeys(bucket).Where(k => k.StartsWith("jobs/job-1/", StringComparison.Ordinal)).ShouldBe(["jobs/job-1/manifest.json"]);
        gcp.PendingWorkerEndCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_queued_request_whose_row_was_relabelled_still_leaves_no_row_when_it_expires()
    {
        var gcp = new FakeGcp().WithFlexStartExpiringAfter(TimeSpan.FromMilliseconds(600));
        var create = gcp.CreateVmAsync(FlexSpec("job-1"), Zone, CancellationToken.None);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var name = FlexSpec("job-1").VmName;
        gcp.BeforeGrantWrite = () => gcp.SimulateExternalRelabel(name, Zone, new Dictionary<string, string> { ["x"] = "y" }); // a CAS changes the queued row after the expiry read it

        var ex = await Should.ThrowAsync<CloudOperationException>(() => create.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        ex.Error.Code.ShouldBe("FLEX_START_REQUEST_EXPIRED");
        (await gcp.GetVmAsync(name, Zone, CancellationToken.None)).ShouldBeNull("no PROVISIONING ghost row stays");
    }

    [Fact]
    public async Task A_deferred_worker_stop_never_stops_a_later_vm_made_under_the_same_name()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Stop);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-1/manifest.json", Manifest);
        var first = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None); // its worker's stop is pending
        gcp.WithWorker(FakeWorkerMode.Never);
        VmDescriptor? later = null;
        gcp.BeforeStopWrite = () =>
        {
            gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None).GetAwaiter().GetResult();
            later = gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
        };

        await gcp.TryDownloadAsync(bucket, "jobs/job-1/result.json", CancellationToken.None); // the worker's stop fires here

        later.ShouldNotBeNull();
        (await gcp.GetVmAsync(later.Name, later.Zone, CancellationToken.None))!.Status.ShouldBe("RUNNING");
    }

    [Fact]
    public async Task A_vm_that_already_existed_has_an_instance_id_so_a_worker_end_binds_to_it()
    {
        var gcp = new FakeGcp().WithAlreadyExists("deg-existing", Zone);

        var vm = await gcp.GetVmAsync("deg-existing", Zone, CancellationToken.None);

        vm!.InstanceId.ShouldNotBeNull();
    }
    [Fact]
    public async Task Created_vm_is_found_by_name_and_zone()
    {
        var gcp = new FakeGcp();

        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldNotBeNull();
        found!.Name.ShouldBe(created.Name);
    }

    [Fact]
    public async Task Stopped_vm_is_reported_as_stopped_not_deleted()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-2"), Zone, CancellationToken.None);

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldNotBeNull();
        found!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task Deleted_vm_is_no_longer_found()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-3"), Zone, CancellationToken.None);

        await gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldBeNull();
    }

    [Fact]
    public async Task A_spec_missing_a_required_label_is_rejected_before_the_fake_creates_anything()
    {
        var gcp = new FakeGcp();
        var spec = ValidSpec("job-4") with { InstallationId = "" };

        await Should.ThrowAsync<InvalidOperationException>(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));

        (await gcp.GetVmAsync(spec.VmName, Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Reports_billing_and_compute_api_enabled_so_preflight_can_pass_in_dev_mode()
    {
        var gcp = new FakeGcp();

        (await gcp.IsBillingEnabledAsync("fake-project", CancellationToken.None)).ShouldBeTrue();
        (await gcp.IsComputeApiEnabledAsync("fake-project", CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Default_gpu_quota_is_one_so_the_unscripted_baseline_still_succeeds()
    {
        var gcp = new FakeGcp();

        (await gcp.GetGpuQuotaAsync("fake-project", "us-central1", "nvidia-l4", CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public void Signed_out_account_reports_no_selected_project()
    {
        var gcp = new FakeGcp().WithSignedOut();

        gcp.IsSignedIn.ShouldBeFalse();
        gcp.SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public void A_fresh_fake_is_not_signed_in_with_no_selected_project()
    {
        // Issue #424: a fresh profile is not signed in to any Google
        // account. The always-signed-in-to-"fake-project" default this
        // fake used to carry contradicted that - a ShellViewModel built
        // against an unscripted FakeGcp would show a signed-in status pill
        // on what is supposed to be a fresh profile.
        var gcp = new FakeGcp();

        gcp.IsSignedIn.ShouldBeFalse();
        gcp.SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public async Task SignInAsync_flips_a_fresh_fake_to_signed_in()
    {
        // Issue #424's own Tests line: "a new FakeGcp instance is not
        // signed in until SignInAsync is called; SignInAsync flips
        // IsSignedIn to true." WizardViewModel.SignInAsync reads
        // IGcpAccount.IsSignedIn right after awaiting this call to decide
        // whether to navigate on - a no-op SignInAsync would leave that
        // flow wired to nothing against this fake.
        var gcp = new FakeGcp();

        await gcp.SignInAsync(CancellationToken.None);

        gcp.IsSignedIn.ShouldBeTrue();
        gcp.SelectedProjectId.ShouldNotBeNull();
    }

    [Fact]
    public void WithSelectedProject_arms_the_fake_as_signed_in_to_that_project()
    {
        var gcp = new FakeGcp().WithSelectedProject("my-project");

        gcp.IsSignedIn.ShouldBeTrue();
        gcp.SelectedProjectId.ShouldBe("my-project");
    }

    [Fact]
    public async Task A_VM_made_again_under_a_name_that_was_deleted_waits_for_its_own_delete_signal()
    {
        var gcp = new FakeGcp();
        var spec = ValidSpec("job-regen");
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        await gcp.DeleteVmAsync(spec.VmName, Zone, CancellationToken.None);
        gcp.WhenVmDeletedAsync(spec.VmName).IsCompleted.ShouldBeTrue("the first delete happened");

        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);

        var second = gcp.WhenVmDeletedAsync(spec.VmName);
        second.IsCompleted.ShouldBeFalse("the VM made again is not deleted yet: the first delete's signal must not answer for it");
        await gcp.DeleteVmAsync(spec.VmName, Zone, CancellationToken.None);
        second.IsCompleted.ShouldBeTrue();
    }
}
