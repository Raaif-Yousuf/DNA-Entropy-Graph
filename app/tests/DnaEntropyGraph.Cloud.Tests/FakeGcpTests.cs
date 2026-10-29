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

    private sealed record VmScan(List<string> Violations, int Marked, int Markers, int HelperWrites, int UpdateVmCalls);

    private static string BlankComments(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", m => new string(' ', m.Length), System.Text.RegularExpressions.RegexOptions.Singleline);

    // The body of a helper method, by brace counting on the comment-free text: (start, end) or (-1, -1).
    private static (int Start, int End) BodyOf(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
        {
            return (-1, -1);
        }

        // Past the parameter list: an expression body (`=>`) ends at its `;`, a block body at its closing brace. Brace counting alone would run an expression-bodied member into the next one.
        var paren = code.IndexOf('(', start);
        var parenDepth = 0;
        var afterParams = paren;
        for (var i = paren; i < code.Length; i++)
        {
            parenDepth += code[i] == '(' ? 1 : code[i] == ')' ? -1 : 0;
            if (parenDepth == 0)
            {
                afterParams = i + 1;
                break;
            }
        }

        if (code[afterParams..].TrimStart().StartsWith("=>", StringComparison.Ordinal))
        {
            var semicolon = code.IndexOf(';', afterParams);
            return semicolon < 0 ? (-1, -1) : (start, semicolon);
        }

        var open = code.IndexOf('{', afterParams);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return (start, i);
            }
        }

        return (-1, -1);
    }

    private static VmScan ScanVmRowWrites(string text, string label)
    {
        var assignment = new System.Text.RegularExpressions.Regex(@"_vms\s*\[(?:[^\[\]]|\[[^\[\]]*\])*\]\s*(?:\?\?|[-+*/%&|^]|<<|>>)?=(?<rhs>[^=][^;]*)");
        var violations = new List<string>();
        var helperWrites = 0;
        var marked = 0;
        var updateVmCalls = System.Text.RegularExpressions.Regex.Matches(text, @"\bUpdateVm\(").Count;
        // Comments blanked to spaces of the same length, so a match's index still addresses the original text (and the marker comment).
        var code = BlankComments(text);
        var markers = System.Text.RegularExpressions.Regex.Matches(text, "vm-row-create").Count;
        var updateBody = BodyOf(code, "VmDescriptor? UpdateVm(");
        var insertBody = BodyOf(code, "bool TryInsertVm(");

        // #918: EVERY mention of the dictionary must be a shape that cannot change a row behind the helpers' back: a read, a removal, a foreach, the declaration, the marked create below,
        // or the one compare-and-swap (TryUpdate) / one insert (TryAdd) inside their own helper. An alias (`var d = _vms;`), a cast, a ref, `.Clear()`, `.AddOrUpdate(...)` are none of these.
        var allowedMember = new System.Text.RegularExpressions.Regex(@"^\s*\.\s*(?<member>\w+)");
        var readMembers = new HashSet<string> { "TryGetValue", "TryRemove", "Values", "Keys", "ContainsKey", "Count", "IsEmpty" };
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"(?<!\w)_vms\b"))
        {
            var before = code[..m.Index].TrimEnd();
            var rest = code[(m.Index + m.Length)..];
            var member = allowedMember.Match(rest);
            var name = member.Success ? member.Groups["member"].Value : null;
            var ok = (System.Text.RegularExpressions.Regex.IsMatch(rest, @"^\s*=\s*new\b") && before.EndsWith('>'))
                || (System.Text.RegularExpressions.Regex.IsMatch(rest, @"^\s*\)") && System.Text.RegularExpressions.Regex.IsMatch(before, @"\bforeach\s*\((?:[^()]|\([^()]*\))*\bin$"))
                || System.Text.RegularExpressions.Regex.IsMatch(rest, @"^\s*\[")
                || (name is not null && readMembers.Contains(name));
            if (!ok && name == "TryUpdate" && updateBody.Start >= 0 && m.Index > updateBody.Start && m.Index < updateBody.End)
            {
                ok = true;
                helperWrites++;
            }
            else if (!ok && name == "TryAdd" && insertBody.Start >= 0 && m.Index > insertBody.Start && m.Index < insertBody.End)
            {
                ok = true;
                helperWrites++;
            }

            if (!ok)
            {
                violations.Add(label + ": " + code.Substring(m.Index, Math.Min(60, code.Length - m.Index)).Replace('\n', ' ').Trim());
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
                violations.Add(label + ": " + m.Value);
            }
        }

        return new VmScan(violations, marked, markers, helperWrites, updateVmCalls);
    }

    [Fact]
    public void Only_a_marked_create_may_assign_a_vm_row_directly_every_change_goes_through_UpdateVm()
    {
        // #875: a bare `_vms[key] = ...` (or AddOrUpdate/GetOrAdd/TryAdd/TryUpdate) is a read-modify-write that can resurrect a deleted VM. FakeGcp is a partial class: every FakeGcp*.cs
        // is scanned and an assignment may span lines. An assignment is allowed only right after a `vm-row-create` marker AND only when it inserts a new row; the compare-and-swap
        // calls are allowed only inside UpdateVm itself. Removals (a delete) are fine anywhere.
        var dir = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "src", "DnaEntropyGraph.Cloud");
        var files = Directory.GetFiles(dir, "FakeGcp*.cs");
        files.Length.ShouldBeGreaterThan(3); // vacuity: the partial files were found
        var scans = files.Select(f => ScanVmRowWrites(File.ReadAllText(f), Path.GetFileName(f))).ToList();

        scans.SelectMany(s => s.Violations).ShouldBeEmpty();
        scans.Sum(s => s.Marked).ShouldBeGreaterThan(0); // vacuity: the creates were found
        scans.Sum(s => s.Markers).ShouldBe(scans.Sum(s => s.Marked)); // every marker sits on a create, and no marker is left on something else
        scans.Sum(s => s.HelperWrites).ShouldBeGreaterThan(0); // vacuity: the helper's own swap was found
        scans.Sum(s => s.UpdateVmCalls).ShouldBeGreaterThan(5); // vacuity: the helper is really used
    }

    [Theory]
    [InlineData("aliasing the dictionary", "void M() { var d = _vms; d[key] = new VmDescriptor(); }")]
    [InlineData("clearing it", "void M() { _vms.Clear(); }")]
    [InlineData("a cast to an interface", "void M() { ((IDictionary<(string, string), VmDescriptor>)_vms)[key] = row; }")]
    [InlineData("a bare indexer assignment", "void M() { _vms[key] = row; }")]
    [InlineData("a TryUpdate outside the helper", "void M() { _vms.TryUpdate(key, a, b); }")]
    [InlineData("passing it by reference", "void M() { Interlocked.Exchange(ref _vms, other); }")]
    [InlineData("a this-qualified clear", "void M() { this._vms.Clear(); }")]
    [InlineData("a this-qualified alias", "void M() { var d = this._vms; }")]
    [InlineData("a null-coalescing indexer assignment", "void M() { _vms[key] ??= row; }")]
    [InlineData("passing it as an in argument", "void M() { Take(in _vms); }")]
    [InlineData("a TryAdd outside the helper", "bool TryInsertVm(k, r) => _vms.TryAdd(k, r); void Other() { _vms.TryAdd(k, r); }")]
    [InlineData("a TryAdd in the member after a block-bodied helper", "bool TryInsertVm(k, r) { return true; } void Other() { _vms.TryAdd(k, r); }")]
    public void The_vm_row_guard_catches_every_bypass_shape(string shape, string code)
    {
        var scan = ScanVmRowWrites("class C { private readonly ConcurrentDictionary<(string, string), VmDescriptor> _vms = new(); " + code + " }", "planted.cs");

        scan.Violations.ShouldNotBeEmpty(shape);
    }

    [Theory]
    [InlineData("a read", "void M() { _vms.TryGetValue(key, out var v); var x = _vms.Values; foreach (var (k, vm) in _vms) { } }")]
    [InlineData("a removal", "void M() { _vms.TryRemove(key, out _); _vms.TryRemove(new KeyValuePair<int, int>(key, row)); }")]
    [InlineData("a marked create", "void M() { // vm-row-create: a new row\n _vms[key] = new VmDescriptor(a); }")]
    [InlineData("the TryAdd inside an expression-bodied helper", "bool TryInsertVm(k, r) => _vms.TryAdd(k, r);")]
    [InlineData("the TryAdd inside a block-bodied helper", "bool TryInsertVm(k, r) { return _vms.TryAdd(k, r); }")]
    public void The_vm_row_guard_lets_reads_removals_and_a_marked_create_through(string shape, string code)
    {
        var scan = ScanVmRowWrites("class C { private readonly ConcurrentDictionary<(string, string), VmDescriptor> _vms = new(); " + code + " }", "planted.cs");

        scan.Violations.ShouldBeEmpty(shape);
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
    public async Task A_reuse_whose_worker_fails_after_writing_its_files_leaves_the_vm_stopped_and_no_worker_files_and_no_pending_end()
    {
        // The worker has already written result.json and queued its self-delete when it fails: the restore-on-exception branch must take them back (#875 L4).
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", Manifest);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        var wrote = false;
        gcp.AfterWorkerWrite = () =>
        {
            wrote = gcp.ObjectKeys(bucket).Contains("jobs/job-2/result.json") && gcp.PendingWorkerEndCount == 1;
            throw new InvalidOperationException("the worker died after writing");
        };

        await Should.ThrowAsync<InvalidOperationException>(() => gcp.ReuseVmAsync(ValidSpec("job-2"), created.Name, created.Zone, CancellationToken.None));

        wrote.ShouldBeTrue("vacuity: the failure came after the worker's writes");
        (await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None))!.Status.ShouldBe("STOPPED");
        gcp.ObjectKeys(bucket).Where(k => k.StartsWith("jobs/job-2/", StringComparison.Ordinal)).ShouldBe(["jobs/job-2/manifest.json"]);
        gcp.PendingWorkerEndCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_reuse_undoes_only_what_the_worker_wrote_and_keeps_what_another_actor_put_under_the_job_prefix()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-2/manifest.json", Manifest);
        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        var manifestAlwaysReadable = true;
        var midFlightReads = 0;
        gcp.BeforeStartWrite = () =>
        {
            // Mid-flight (the worker has run, the start has not landed): the manifest is readable here too, not only after the failure (#918).
            manifestAlwaysReadable &= gcp.TryDownloadAsync(bucket, "jobs/job-2/manifest.json", CancellationToken.None).GetAwaiter().GetResult() is not null;
            midFlightReads++;
            gcp.PutObject(bucket, "jobs/job-2/cancel.marker", "x"u8.ToArray()); // an app write that lands while the reuse is in flight
            gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None).GetAwaiter().GetResult();
        };

        await Should.ThrowAsync<CloudOperationException>(() => gcp.ReuseVmAsync(ValidSpec("job-2"), created.Name, created.Zone, CancellationToken.None));
        manifestAlwaysReadable &= await gcp.TryDownloadAsync(bucket, "jobs/job-2/manifest.json", CancellationToken.None) is not null;

        gcp.ObjectKeys(bucket).Where(k => k.StartsWith("jobs/job-2/", StringComparison.Ordinal)).Order().ShouldBe(["jobs/job-2/cancel.marker", "jobs/job-2/manifest.json"]);
        midFlightReads.ShouldBe(1); // vacuity: the in-flight read really ran
        manifestAlwaysReadable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_plan_scheduled_on_a_queued_flex_row_follows_the_row_to_the_granted_vm()
    {
        var gcp = new FakeGcp().WithFlexStartHeld();
        var create = gcp.CreateVmAsync(FlexSpec("job-1"), Zone, CancellationToken.None);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var name = FlexSpec("job-1").VmName;
        gcp.WithPreemption(name, Zone, TimeSpan.Zero); // binds to the queued row
        gcp.GrantFlexStart();
        await create.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var vm = await gcp.GetVmAsync(name, Zone, CancellationToken.None);

        vm!.Status.ShouldBe("TERMINATED");
        vm.StatusReason.ShouldBe("preempted");
    }

    [Fact]
    public async Task A_stop_of_a_gone_incarnation_does_not_restart_the_stopping_countdown_of_the_vm_that_replaced_it()
    {
        var gcp = new FakeGcp().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Stop).WithStopBehaviour(stoppingPolls: 2);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-1/manifest.json", Manifest);
        var first = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None); // its worker's stop is pending
        gcp.WithWorker(FakeWorkerMode.Never);
        gcp.BeforeStopWrite = () =>
        {
            gcp.BeforeStopWrite = null;
            gcp.DeleteVmAsync(first.Name, first.Zone, CancellationToken.None).GetAwaiter().GetResult();
            gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None).GetAwaiter().GetResult();
            gcp.StopVmAsync(first.Name, first.Zone, CancellationToken.None).GetAwaiter().GetResult(); // the replacement is STOPPING
            gcp.GetVmAsync(first.Name, first.Zone, CancellationToken.None).GetAwaiter().GetResult(); // one poll of its countdown
        };

        await gcp.TryDownloadAsync(bucket, "jobs/job-1/result.json", CancellationToken.None); // the first worker's stop fires here, on a VM that is gone
        await gcp.GetVmAsync(first.Name, first.Zone, CancellationToken.None);
        var vm = await gcp.GetVmAsync(first.Name, first.Zone, CancellationToken.None);

        vm!.Status.ShouldBe("STOPPED", "the replacement's own countdown was not restarted by the stale stop");
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
        var gcp = new FakeGcp().WithFlexStartHeld().WithWorkerEndingItsOwnVm(WorkerSelfEnd.Delete);
        var bucket = FakeGcp.BucketName("fake-project");
        gcp.PutObject(bucket, "jobs/job-1/manifest.json", Manifest);
        var create = gcp.CreateVmAsync(FlexSpec("job-1"), Zone, CancellationToken.None);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var name = FlexSpec("job-1").VmName;
        var pendingAtDelete = -1;
        gcp.BeforeGrantWrite = () =>
        {
            pendingAtDelete = gcp.PendingWorkerEndCount; // the worker's self-delete is queued by now: there is something to undo
            gcp.DeleteVmAsync(name, Zone, CancellationToken.None).GetAwaiter().GetResult();
        };
        gcp.GrantFlexStart();

        await Should.ThrowAsync<CloudOperationException>(() => create.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        pendingAtDelete.ShouldBe(1);

        (await gcp.TryDownloadAsync(bucket, "jobs/job-1/result.json", CancellationToken.None)).ShouldBeNull();
        gcp.ObjectKeys(bucket).Where(k => k.StartsWith("jobs/job-1/", StringComparison.Ordinal)).ShouldBe(["jobs/job-1/manifest.json"]);
        gcp.PendingWorkerEndCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_queued_request_whose_row_was_relabelled_still_leaves_no_row_when_it_expires()
    {
        var gcp = new FakeGcp().WithFlexStartExpiringAfter(TimeSpan.FromMilliseconds(600));
        var name = FlexSpec("job-1").VmName;
        var relabelled = false;
        // Installed BEFORE the request is made, so a loaded box cannot let the expiry run past an uninstalled hook.
        gcp.BeforeGrantWrite = () =>
        {
            gcp.SimulateExternalRelabel(name, Zone, new Dictionary<string, string> { ["x"] = "y" }); // a CAS changes the queued row after the expiry read it
            relabelled = true;
        };
        var create = gcp.CreateVmAsync(FlexSpec("job-1"), Zone, CancellationToken.None);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => create.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        relabelled.ShouldBeTrue("the relabel ran between the expiry's read and its removal");
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
