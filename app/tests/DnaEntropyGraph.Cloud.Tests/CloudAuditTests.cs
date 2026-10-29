using System.Text.Json;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cloud.Audit;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #245: every mutating cloud call leaves one audit entry (operation, resource, ids, outcome class, duration), reads leave none,
/// and nothing that identifies the user or their files reaches an entry.
/// </summary>
public class CloudAuditTests
{
    private const string Secret = "patient-zero-brca";

    private sealed class MemoryLog : ICloudAuditLog
    {
        /// <summary>Finished entries only; the started lines are in <see cref="Started"/>.</summary>
        public List<CloudAuditEntry> Entries { get; } = [];

        public List<CloudAuditEntry> Started { get; } = [];

        public void Record(CloudAuditEntry entry) => (entry.Phase == "started" ? Started : Entries).Add(entry);
    }

    private sealed class ThrowingLog : ICloudAuditLog
    {
        public void Record(CloudAuditEntry entry) => throw new IOException("disk full");
    }

    private static VmSpec Spec(string jobId) => new(
        ProjectId: "my-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static (CloudJobRunner Runner, FakeGcp Gcp, MemoryLog Log) Rig(FakeGcp? gcp = null, ICloudAuditLog? sink = null)
    {
        gcp ??= new FakeGcp();
        var log = new MemoryLog();
        var auditor = new CloudAuditor(sink ?? log, () => "install-1", TimeProvider.System);
        var runner = new CloudJobRunner(
            new AuditedComputeGateway(gcp, auditor),
            new AuditedStorageGateway(gcp, auditor),
            new AuditedProjectSetupGateway(gcp, auditor),
            gcp,
            new AuditedWorkerIdentityGateway(gcp, auditor),
            new InMemoryRunRepository());
        return (runner, gcp, log);
    }

    private static CloudJobRequest Request(string jobId, AfterTaskAction after = AfterTaskAction.Stop)
        => TestInputs.Request(jobId, Spec(jobId), null, after, null, Secret + ".gb");

    [Fact]
    public async Task A_full_run_records_the_expected_mutations_in_order_with_ids_and_no_reads()
    {
        var (runner, _, log) = Rig();

        var result = await runner.RunAsync(Request("job-1"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        var ops = log.Entries.Select(e => e.Operation).ToList();
        ops.ShouldContain("Storage.EnsureBucket");
        ops.ShouldContain("Storage.Upload");
        ops.ShouldContain("Compute.CreateVm");
        ops.ShouldContain("Compute.StopVm");
        ops.IndexOf("Storage.EnsureBucket").ShouldBeLessThan(ops.IndexOf("Compute.CreateVm"));
        ops.IndexOf("Compute.CreateVm").ShouldBeLessThan(ops.IndexOf("Compute.StopVm"));
        log.Entries.ShouldAllBe(e => e.Outcome == "ok");
        log.Entries.ShouldAllBe(e => e.InstallationId == "install-1");

        // No read ever lands in the log.
        ops.ShouldNotContain(o => o.Contains("Get", StringComparison.Ordinal) || o.Contains("Find", StringComparison.Ordinal) || o.Contains("List", StringComparison.Ordinal) || o.Contains("Download", StringComparison.Ordinal));

        var create = log.Entries.Single(e => e.Operation == "Compute.CreateVm");
        create.JobId.ShouldBe("job-1");
        create.Resource.ShouldContain("deg-job-1");
        create.RequestId.ShouldNotBeNullOrEmpty();
        log.Entries.Single(e => e.Operation == "Compute.StopVm").JobId.ShouldBe("job-1");
        log.Entries.Where(e => e.Operation == "Storage.Upload").ShouldAllBe(e => e.Resource.EndsWith("/jobs/job-1", StringComparison.Ordinal) && e.JobId == "job-1");
    }

    [Fact]
    public async Task Through_the_fake_the_started_and_finished_lines_of_a_CreateVm_carry_the_same_request_id()
    {
        // Issue #245 review M1: the fake used to overwrite the id the auditor minted before the started line.
        var (runner, _, log) = Rig();

        await runner.RunAsync(Request("job-m1"), CancellationToken.None);

        var started = log.Started.Single(e => e.Operation == "Compute.CreateVm");
        var finished = log.Entries.Single(e => e.Operation == "Compute.CreateVm");
        started.RequestId.ShouldNotBeNullOrEmpty();
        finished.RequestId.ShouldBe(started.RequestId);
    }

    [Fact]
    public async Task A_timeout_while_the_callers_token_is_not_cancelled_is_not_recorded_as_cancelled()
    {
        // Issue #245 review M2: an HttpClient timeout surfaces as TaskCanceledException with the caller's token untouched.
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => null, TimeProvider.System);

        await Should.ThrowAsync<TaskCanceledException>(() => auditor.RunAsync<int>("Test.Op", () => ("r", null), () => throw new TaskCanceledException("timed out"), cancellationToken: CancellationToken.None));

        log.Entries.Single().Outcome.ShouldBe("timeout");
    }

    [Fact]
    public async Task A_cancellation_the_caller_asked_for_is_still_recorded_as_cancelled()
    {
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => null, TimeProvider.System);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => auditor.RunAsync<int>("Test.Op", () => ("r", null), () => throw new OperationCanceledException(cts.Token), cancellationToken: cts.Token));

        log.Entries.Single().Outcome.ShouldBe("cancelled");
    }

    [Fact]
    public async Task A_decorator_passes_the_callers_token_so_a_timeout_is_told_from_a_cancel()
    {
        var log = new MemoryLog();
        var inner = Substitute.For<IComputeGateway>();
        inner.StopVmAsync("deg-j", "z", Arg.Any<CancellationToken>()).Returns(Task.FromException(new TaskCanceledException("timed out")));
        var gateway = new AuditedComputeGateway(inner, new CloudAuditor(log, () => null, TimeProvider.System));

        await Should.ThrowAsync<TaskCanceledException>(() => gateway.StopVmAsync("deg-j", "z", CancellationToken.None));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<TaskCanceledException>(() => gateway.StopVmAsync("deg-j", "z", cts.Token));

        log.Entries.Select(e => e.Outcome).ShouldBe(["timeout", "cancelled"]);
    }

    private static async Task<CloudAuditEntry> SingleEntryAsync(MemoryLog log)
    {
        // The decorator records when the abandoned inner call finally ends, which can be after the caller already saw the failure.
        for (var i = 0; i < 500 && log.Entries.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return log.Entries.Single();
    }

    [Fact]
    public async Task The_apps_own_call_deadline_through_GatewayCalls_is_recorded_as_timeout_not_cancelled()
    {
        // Issue #245 review round 2 (M): GatewayCalls hands the gateway a LINKED deadline token, so the deadline firing made the auditor's token cancelled.
        var log = new MemoryLog();
        var inner = Substitute.For<IComputeGateway>();
        inner.StopVmAsync("deg-j", "z", Arg.Any<CancellationToken>()).Returns(ci => Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>()));
        var gateway = new AuditedComputeGateway(inner, new CloudAuditor(log, () => null, TimeProvider.System));
        var calls = new GatewayCalls(new CloudRunSettings { CallTimeout = TimeSpan.FromMilliseconds(50) }, new FakeGcp());

        var failure = await Should.ThrowAsync<CloudOperationException>(() => calls.CallAsync(async token =>
            {
                await gateway.StopVmAsync("deg-j", "z", token);
                return 0;
            }, CancellationToken.None));

        failure.Error.Code.ShouldBe("TIMEOUT");
        (await SingleEntryAsync(log)).Outcome.ShouldBe("timeout");
    }

    private sealed class SignallingLog : ICloudAuditLog
    {
        private readonly TaskCompletionSource<CloudAuditEntry> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CloudAuditEntry> Finished => _finished.Task;

        public void Record(CloudAuditEntry entry)
        {
            if (entry.Phase != "started")
            {
                _finished.TrySetResult(entry);
            }
        }
    }

    [Fact]
    public async Task A_user_cancel_through_GatewayCalls_reaches_the_call_it_abandoned_and_is_recorded_as_cancelled()
    {
        // The inner call ends ONLY when the token it was handed (GatewayCalls' linked token) is cancelled, so the audit entry exists only if the user's cancel was relayed to it.
        var log = new SignallingLog();
        using var cts = new CancellationTokenSource();
        var inner = Substitute.For<IComputeGateway>();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.StopVmAsync("deg-j", "z", Arg.Any<CancellationToken>()).Returns(ci =>
        {
            reached.SetResult();
            return Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>());
        });
        var gateway = new AuditedComputeGateway(inner, new CloudAuditor(log, () => null, TimeProvider.System));
        var calls = new GatewayCalls(new CloudRunSettings { CallTimeout = TimeSpan.FromSeconds(30) }, new FakeGcp());

        var pending = calls.CallAsync(async token =>
            {
                await gateway.StopVmAsync("deg-j", "z", token);
                return 0;
            }, cts.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        var entry = await log.Finished.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        entry.Outcome.ShouldBe("cancelled");
    }

    [Fact]
    public async Task A_user_cancel_stays_a_cancel_when_a_callback_the_call_registered_throws()
    {
        // CancelAsync runs the callee's callbacks; one that throws surfaces as an AggregateException that must not replace the cancellation (callers filter on it).
        var calls = new GatewayCalls(new CloudRunSettings { CallTimeout = TimeSpan.FromSeconds(30) }, new FakeGcp());
        using var cts = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = calls.CallAsync<int>(async token =>
            {
                token.Register(() => throw new InvalidOperationException("callee callback"));
                reached.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }, cts.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            await cts.CancelAsync();
        }
        catch (AggregateException)
        {
            // The link may hand the throwing callback to the canceller; the test is about what CallAsync reports.
        }

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Stopping_an_abandoned_call_swallows_a_callback_that_throws_and_still_cancels()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Token.Register(() => throw new InvalidOperationException("callee callback"));

        await CloudAuditContext.StopAbandonedAsync(deadline, null);

        deadline.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task An_abandoned_call_that_faults_later_is_observed_and_never_reaches_UnobservedTaskException()
    {
        var marker = new InvalidOperationException("abandoned " + Guid.NewGuid());
        var seen = false;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => ReferenceEquals(x, marker)))
            {
                seen = true;
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await AbandonThenFaultAsync(marker);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            seen.ShouldBeFalse("the fault of a call nobody waits for any more must be observed");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonThenFaultAsync(Exception marker)
    {
        var source = new TaskCompletionSource<int>();
        using var deadline = new CancellationTokenSource();
        await CloudAuditContext.StopAbandonedAsync(deadline, source.Task);
        source.SetException(marker);
    }

    [Fact]
    public async Task The_installation_id_is_read_once_and_a_null_is_retried_not_cached()
    {
        // Issue #245 review M3.
        var reads = 0;
        string? answer = null;
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => { reads++; return answer; }, TimeProvider.System);

        await auditor.RunAsync("Test.Op", () => ("r", null), () => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);
        log.Entries[0].InstallationId.ShouldBeNull();
        answer = "install-9";
        await auditor.RunAsync("Test.Op", () => ("r", null), () => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);
        await auditor.RunAsync("Test.Op", () => ("r", null), () => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);

        log.Entries[1].InstallationId.ShouldBe("install-9");
        log.Entries[2].InstallationId.ShouldBe("install-9");
        reads.ShouldBe(2, "a null is asked again, a real id exactly once");
    }

    [Fact]
    public async Task An_audited_EndVm_passes_the_acceptance_callback_to_the_real_gateway_and_is_recorded_as_the_stop_it_is()
    {
        // The interface default of EndVmAsync reports acceptance only when the work is done; the audit decorator must forward to the inner gateway's own, or a real
        // gateway's earlier "accepted" moment (issue #113) is lost whenever the audit wraps it.
        var log = new MemoryLog();
        var inner = Substitute.For<IComputeGateway>();
        var accepted = 0;
        inner.EndVmAsync("deg-job-7", "z", false, Arg.Any<Action?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.ArgAt<Action?>(3)?.Invoke();
            return Task.CompletedTask;
        });

        await new AuditedComputeGateway(inner, new CloudAuditor(log, () => "i", TimeProvider.System)).EndVmAsync("deg-job-7", "z", delete: false, () => accepted++, CancellationToken.None);

        accepted.ShouldBe(1);
        await inner.Received(1).EndVmAsync("deg-job-7", "z", false, Arg.Any<Action?>(), Arg.Any<CancellationToken>());
        var entry = log.Entries.Single();
        entry.Operation.ShouldBe("Compute.StopVm");
        entry.JobId.ShouldBe("job-7");
    }

    [Fact]
    public async Task A_failing_mutation_records_its_classifier_bucket_and_error_code()
    {
        var gcp = new FakeGcp().WithZoneStockout("us-central1-a");
        var (runner, _, log) = Rig(gcp);

        await runner.RunAsync(Request("job-2"), CancellationToken.None);

        var create = log.Entries.First(e => e.Operation == "Compute.CreateVm");
        create.Outcome.ShouldBe("stockout");
        create.ErrorCode.ShouldBe("ZONE_RESOURCE_POOL_EXHAUSTED");
    }

    [Fact]
    public async Task A_permission_failure_is_recorded_as_permission()
    {
        var gcp = new FakeGcp().WithPermissionDenied("my-project");
        var (runner, _, log) = Rig(gcp);

        await runner.RunAsync(Request("job-3"), CancellationToken.None);

        log.Entries.First(e => e.Operation == "Compute.CreateVm").Outcome.ShouldBe("permission");
    }

    [Fact]
    public async Task No_entry_names_an_input_file_the_email_or_a_message()
    {
        var (runner, _, log) = Rig(new FakeGcp().WithZoneStockout("us-central1-a"));

        await runner.RunAsync(Request("job-4"), CancellationToken.None);
        var (okRunner, _, okLog) = Rig();
        await okRunner.RunAsync(Request("job-5"), CancellationToken.None);

        var text = JsonSerializer.Serialize(log.Entries.Concat(okLog.Entries));
        text.ShouldNotBeEmpty();
        text.ShouldNotContain(Secret, Case.Insensitive);
        text.ShouldNotContain("seq.gb", Case.Insensitive);
        text.ShouldNotContain("@");
        text.ShouldNotContain("fake-user", Case.Insensitive);
    }

    [Fact]
    public async Task A_log_that_throws_never_fails_the_run()
    {
        var (runner, _, _) = Rig(sink: new ThrowingLog());

        var result = await runner.RunAsync(Request("job-6"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
    }

    [Fact]
    public async Task The_caller_still_sees_the_original_exception_after_it_is_recorded()
    {
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => null, TimeProvider.System);

        var thrown = await Should.ThrowAsync<CloudOperationException>(() => auditor.RunAsync<int>(
            "Test.Op",
            () => ("r", null),
            () => throw new CloudOperationException(new CloudError("QUOTA_EXCEEDED", 403, "message with a@b.com"), CloudErrorKind.Quota)));

        thrown.Kind.ShouldBe(CloudErrorKind.Quota);
        log.Entries.Single().Outcome.ShouldBe("quota");
        log.Entries.Single().ErrorCode.ShouldBe("QUOTA_EXCEEDED");
        JsonSerializer.Serialize(log.Entries).ShouldNotContain("a@b.com");
    }

    // Closed lists (issue #245 review): every method of every I*Gateway interface in Core is named here as a mutation (with the operation
    // its audit entry must carry) or a read. A method, or a whole gateway interface, that is not listed fails until someone decides which
    // it is and, for a mutation, routes it through CloudAuditor.
    private static readonly Dictionary<Type, ((string Method, string Operation)[] Mutations, string[] Reads)> Classified = new()
    {
        [typeof(IComputeGateway)] = (
            [
                ("CreateVmAsync", "Compute.CreateVm"), ("ReuseVmAsync", "Compute.ReuseVm"), ("StartVmAsync", "Compute.StartVm"),
                ("StopVmAsync", "Compute.StopVm"), ("DeleteVmAsync", "Compute.DeleteVm"), ("DeleteDiskAsync", "Compute.DeleteDisk"),
                ("EndVmAsync", "Compute.DeleteVm"), // ArgFor passes delete: true
            ],
            ["ListAcceleratorZonesAsync", "ListDisksByInstallationAsync", "GetVmAsync", "FindByJobIdAsync", "ListByInstallationAsync", "ListAppVmsAsync", "WasPreemptedAsync"]),
        [typeof(IStorageGateway)] = (
            [("EnsureBucketAsync", "Storage.EnsureBucket"), ("UploadAsync", "Storage.Upload"), ("DeletePrefixAsync", "Storage.DeletePrefix"), ("DeleteObjectAsync", "Storage.DeleteObject")],
            ["DownloadAsync", "TryDownloadAsync", "ListBucketsAsync", "GetPrefixUsageAsync", "ListChildPrefixesAsync"]),
        [typeof(IProjectSetupGateway)] = ([("EnableComputeApiAsync", "ProjectSetup.EnableComputeApi")], ["GetProjectStateAsync", "IsBillingEnabledAsync", "IsComputeApiEnabledAsync"]),
        [typeof(IWorkerIdentityGateway)] = ([("EnsureWorkerIdentityAsync", "Iam.EnsureWorkerIdentity")], []),
        [typeof(IServiceEnablementGateway)] = ([("EnableServicesAsync", "ServiceUsage.EnableServices")], ["IsServiceEnabledAsync"]),
        [typeof(IBillingGateway)] = ([("LinkProjectAsync", "Billing.LinkProject")], ["GetBillingStatusAsync", "ListOpenBillingAccountsAsync"]),
        [typeof(IProjectCatalogGateway)] = ([("CreateProjectAsync", "ProjectCatalog.CreateProject")], ["ListActiveProjectsAsync", "GetProjectAsync"]),
        [typeof(IQuotaGateway)] = ([("RequestQuotaIncreaseAsync", "Quota.RequestQuotaIncrease")], ["GetGpuQuotaAsync", "ReadQuotaAsync", "GetQuotaIncreaseEligibilityAsync"]),
    };

    [Fact]
    public void Every_gateway_interface_in_Core_is_classified_so_a_new_one_cannot_dodge_the_audit()
    {
        var found = typeof(IComputeGateway).Assembly.GetTypes()
            .Where(t => t.IsInterface && System.Text.RegularExpressions.Regex.IsMatch(t.Name, @"^I\w*Gateway$"))
            .ToList();
        found.Count.ShouldBeGreaterThanOrEqualTo(8, "the scan must find the gateways, or this guard is vacuous");
        found.Except(Classified.Keys).ShouldBeEmpty("classify the new gateway interface in CloudAuditTests.Classified and add its Audited* decorator");
        Classified.Keys.Except(found).ShouldBeEmpty("a classified interface that no longer exists is a stale entry");
    }

    [Fact]
    public void Every_method_of_every_gateway_interface_is_classified_as_a_mutation_or_a_read()
    {
        foreach (var (type, (mutations, reads)) in Classified)
        {
            var actual = type.GetMethods().Select(m => m.Name).Order().ToArray();
            actual.ShouldBe(mutations.Select(m => m.Method).Concat(reads).Order().ToArray(), type.Name + ": classify the new method in CloudAuditTests.Classified (and audit it if it mutates)");
        }
    }

    [Fact]
    public void Every_interface_method_including_default_ones_is_forwarded_by_its_decorator_itself()
    {
        // A default interface method the decorator does not override compiles silently and BYPASSES the inner gateway's real override.
        var decorators = typeof(AuditedComputeGateway).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IAuditedGateway).IsAssignableFrom(t)).ToList();
        decorators.Count.ShouldBeGreaterThanOrEqualTo(8, "the scan must find the decorators, or this guard is vacuous");
        var checkedMethods = 0;
        foreach (var type in Classified.Keys)
        {
            var decorator = decorators.Single(d => type.IsAssignableFrom(d));
            var map = decorator.GetInterfaceMap(type);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                map.TargetMethods[i].DeclaringType.ShouldBe(decorator, decorator.Name + " must override " + map.InterfaceMethods[i].Name + " itself and forward it");
                checkedMethods++;
            }
        }

        checkedMethods.ShouldBeGreaterThan(30);
    }

    private static object? ArgFor(Type type) => type switch
    {
        _ when type == typeof(CancellationToken) => CancellationToken.None,
        _ when type == typeof(bool) => true,
        _ when type == typeof(Action) => null,
        _ when type == typeof(string) => "jobs/job-9/in/x.gb",
        _ when type == typeof(Stream) => new MemoryStream(),
        _ when type == typeof(VmSpec) => Spec("job-9"),
        _ when type == typeof(VmIncarnation) => new VmIncarnation(1UL, null),
        _ when type == typeof(QuotaIncreaseRequest) => new QuotaIncreaseRequest("NVIDIA_L4_GPUS", "us-central1", 1, "pref-1", "JUSTIFICATION-" + Secret),
        _ when type == typeof(IReadOnlyList<string>) => new[] { "compute.googleapis.com" },
        _ => throw new NotSupportedException("teach CloudAuditTests.ArgFor to build a " + type.Name),
    };

    private static Task Invoke(object target, string method)
    {
        var info = target.GetType().GetMethod(method)!;
        return (Task)info.Invoke(target, info.GetParameters().Select(p => ArgFor(p.ParameterType)).ToArray())!;
    }

    public static TheoryData<string> ClassifiedGateways()
    {
        var data = new TheoryData<string>();
        foreach (var type in Classified.Keys)
        {
            data.Add(type.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ClassifiedGateways))]
    public async Task Every_classified_mutation_has_an_audited_decorator_that_records_it(string gatewayName)
    {
        // Each interface's decorator is found by reflection in the Cloud assembly (it must exist), run over a stub inner, and every
        // Mutation it lists must leave exactly one finished entry with the listed operation; every Read must leave none.
        var type = Classified.Keys.Single(t => t.Name == gatewayName);
        var (mutations, reads) = Classified[type];
        var decoratorType = typeof(AuditedComputeGateway).Assembly.GetTypes()
            .SingleOrDefault(t => t.IsClass && !t.IsAbstract && type.IsAssignableFrom(t) && typeof(IAuditedGateway).IsAssignableFrom(t));
        decoratorType.ShouldNotBeNull(type.Name + " has no Audited* decorator in DnaEntropyGraph.Cloud");

        foreach (var (method, operation) in mutations)
        {
            var log = new MemoryLog();
            var inner = Substitute.For([type], []);
            var decorator = Activator.CreateInstance(decoratorType, inner, new CloudAuditor(log, () => "i", TimeProvider.System))!;
            await Invoke(decorator, method);
            log.Entries.Select(e => e.Operation).ShouldBe([operation], type.Name + "." + method + " must record exactly one " + operation);
            JsonSerializer.Serialize(log.Entries.Concat(log.Started)).ShouldNotContain("JUSTIFICATION");
        }

        foreach (var read in reads)
        {
            var log = new MemoryLog();
            var decorator = Activator.CreateInstance(decoratorType, Substitute.For([type], []), new CloudAuditor(log, () => "i", TimeProvider.System))!;
            await Invoke(decorator, read);
            log.Entries.ShouldBeEmpty(type.Name + "." + read + " is a read and must not be logged");
            log.Started.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Decorated_mutations_keep_their_resource_naming_without_leaking_the_input()
    {
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => "i", TimeProvider.System);
        var storage = Substitute.For<IStorageGateway>();
        storage.EnsureBucketAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("b");
        var ct = CancellationToken.None;

        await new AuditedStorageGateway(storage, auditor).EnsureBucketAsync("p", ct);
        await new AuditedStorageGateway(storage, auditor).DeletePrefixAsync("b", "jobs/job-9/", ct);
        await new AuditedStorageGateway(storage, auditor).UploadAsync("b", "jobs/job-9/in/" + Secret + ".gb", new MemoryStream(), ct);
        await new AuditedQuotaGateway(Substitute.For<IQuotaGateway>(), auditor).RequestQuotaIncreaseAsync("p", new QuotaIncreaseRequest("NVIDIA_L4_GPUS", "us-central1", 1, "pref-1", "JUSTIFICATION-" + Secret), ct);

        log.Entries.Select(e => e.Resource).ShouldBe(
            ["buckets/b", "buckets/b/jobs/job-9", "buckets/b/jobs/job-9", "projects/p/quotas/NVIDIA_L4_GPUS/regions/us-central1"]);
        JsonSerializer.Serialize(log.Entries).ShouldNotContain(Secret);
    }

    [Fact]
    public async Task The_multi_step_non_replayable_mutations_also_write_a_started_line()
    {
        // Creating the bucket, deleting a prefix and creating the worker identity each span several API calls and cannot simply be
        // replayed: a process killed in the middle must leave evidence that they began (the VM calls are covered by the neighbours).
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => "i", TimeProvider.System);
        var storage = Substitute.For<IStorageGateway>();
        storage.EnsureBucketAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("b");
        var ct = CancellationToken.None;

        await new AuditedStorageGateway(storage, auditor).EnsureBucketAsync("p", ct);
        await new AuditedStorageGateway(storage, auditor).DeletePrefixAsync("b", "jobs/job-9/", ct);
        await new AuditedWorkerIdentityGateway(Substitute.For<IWorkerIdentityGateway>(), auditor).EnsureWorkerIdentityAsync("p", "b", ct);

        log.Started.Select(e => e.Operation).ShouldBe(["Storage.EnsureBucket", "Storage.DeletePrefix", "Iam.EnsureWorkerIdentity"]);
        log.Entries.Select(e => e.CorrelationId).ShouldBe(log.Started.Select(e => e.CorrelationId));
    }

    [Fact]
    public async Task Long_mutations_write_a_started_line_before_the_call_and_a_finished_line_after_with_the_same_correlation_id()
    {
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => "i", TimeProvider.System);
        var gate = new TaskCompletionSource<VmDescriptor>();
        var compute = Substitute.For<IComputeGateway>();
        compute.CreateVmAsync(Arg.Any<VmSpec>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(gate.Task);
        var audited = new AuditedComputeGateway(compute, auditor);

        var pending = audited.CreateVmAsync(Spec("job-8"), "z", CancellationToken.None);

        // A process killed here leaves this evidence behind.
        log.Started.Single().Operation.ShouldBe("Compute.CreateVm");
        log.Started.Single().Phase.ShouldBe("started");
        log.Started.Single().JobId.ShouldBe("job-8");
        log.Entries.ShouldBeEmpty();

        gate.SetException(new CloudOperationException(new CloudError("ZONE_RESOURCE_POOL_EXHAUSTED", 503, "x"), CloudErrorKind.Stockout));
        await Should.ThrowAsync<CloudOperationException>(() => pending);

        log.Entries.Single().Phase.ShouldBe("finished");
        log.Entries.Single().Outcome.ShouldBe("stockout");
        log.Entries.Single().CorrelationId.ShouldBe(log.Started.Single().CorrelationId);
        log.Started.Single().CorrelationId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_short_mutation_writes_only_the_finished_line()
    {
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => "i", TimeProvider.System);

        await new AuditedComputeGateway(Substitute.For<IComputeGateway>(), auditor).StopVmAsync("deg-j", "z", CancellationToken.None);

        log.Started.ShouldBeEmpty();
        log.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_null_argument_is_a_recorded_failure_and_not_a_synchronous_throw_out_of_the_decorator()
    {
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => "i", TimeProvider.System);
        var compute = Substitute.For<IComputeGateway>();
        compute.CreateVmAsync(null!, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<VmDescriptor>>(_ => throw new ArgumentNullException("spec"));
        var audited = new AuditedComputeGateway(compute, auditor);

        Task<VmDescriptor> task = null!;
        Should.NotThrow((Action)(() => { task = audited.CreateVmAsync(null!, "z", CancellationToken.None); }));
        await Should.ThrowAsync<ArgumentNullException>(() => task);

        log.Entries.Single().Outcome.ShouldBe("other");
        log.Entries.Single().Resource.ShouldBe("(unknown)");
    }

    [Fact]
    public async Task A_nested_call_restores_the_outer_calls_ids_when_it_ends()
    {
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => "i", TimeProvider.System);

        await auditor.RunAsync("Outer", () => ("r", null), async () =>
        {
            await auditor.RunAsync("Inner", () => ("r2", null), () =>
            {
                CloudAuditContext.Note("inner-req", null);
                return Task.FromResult(1);
            });
            CloudAuditContext.Note("outer-req", null);
            return 1;
        }, cancellationToken: TestContext.Current.CancellationToken);

        log.Entries.Single(e => e.Operation == "Inner").RequestId.ShouldBe("inner-req");
        log.Entries.Single(e => e.Operation == "Outer").RequestId.ShouldBe("outer-req");
    }
    [Fact]
    public async Task Decorators_route_each_mutating_call_to_the_inner_gateway_and_record_it()
    {
        var gcp = new FakeGcp();
        var log = new MemoryLog();
        var auditor = new CloudAuditor(log, () => "install-1", TimeProvider.System);

        await new AuditedProjectSetupGateway(gcp, auditor).EnableComputeApiAsync("my-project", CancellationToken.None);
        await new AuditedServiceEnablementGateway(gcp, auditor).EnableServicesAsync("my-project", ["compute.googleapis.com", "storage.googleapis.com"], CancellationToken.None);
        await new AuditedWorkerIdentityGateway(gcp, auditor).EnsureWorkerIdentityAsync("my-project", "bucket-1", CancellationToken.None);
        _ = await new AuditedStorageGateway(gcp, auditor).ListBucketsAsync("my-project", CancellationToken.None);

        log.Entries.Select(e => e.Operation).ToList().ShouldBe(["ProjectSetup.EnableComputeApi", "ServiceUsage.EnableServices", "Iam.EnsureWorkerIdentity"]);
        log.Entries.ShouldAllBe(e => e.Outcome == "ok");
        log.Entries[2].Resource.ShouldBe("projects/my-project/buckets/bucket-1");
    }

    [Fact]
    public void Object_prefix_stops_at_the_job_and_never_includes_a_file_name()
    {
        CloudAuditor.ObjectPrefix("jobs/job-1/input/" + Secret + ".gb").ShouldBe(("jobs/job-1", "job-1"));
        CloudAuditor.ObjectPrefix(Secret + ".gb").ShouldBe(("(other)", null));
        CloudAuditor.ObjectPrefix("jobs/a@b.com/x/y").ShouldBe(("(other)", null));
    }
}
