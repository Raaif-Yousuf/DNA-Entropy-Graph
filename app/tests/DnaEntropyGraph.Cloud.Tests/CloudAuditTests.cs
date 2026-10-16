using System.Text.Json;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cloud.Audit;
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
        public List<CloudAuditEntry> Entries { get; } = [];

        public void Record(CloudAuditEntry entry) => Entries.Add(entry);
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
        create.RequestId.ShouldBe("job-1");
        log.Entries.Single(e => e.Operation == "Compute.StopVm").JobId.ShouldBe("job-1");
        log.Entries.Where(e => e.Operation == "Storage.Upload").ShouldAllBe(e => e.Resource.EndsWith("/jobs/job-1", StringComparison.Ordinal) && e.JobId == "job-1");
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
            "r",
            null,
            () => throw new CloudOperationException(new CloudError("QUOTA_EXCEEDED", 403, "message with a@b.com"), CloudErrorKind.Quota)));

        thrown.Kind.ShouldBe(CloudErrorKind.Quota);
        log.Entries.Single().Outcome.ShouldBe("quota");
        log.Entries.Single().ErrorCode.ShouldBe("QUOTA_EXCEEDED");
        JsonSerializer.Serialize(log.Entries).ShouldNotContain("a@b.com");
    }

    [Fact]
    public void Every_mutating_method_of_every_gateway_interface_is_known_to_the_audit()
    {
        // A method added to a gateway interface that is not a read must be added here AND routed through CloudAuditor in AuditedGateways.cs.
        string[] readPrefixes = ["Get", "List", "Find", "Is", "TryDownload", "Download", "Read"];
        var audited = new Dictionary<Type, string[]>
        {
            [typeof(IComputeGateway)] = ["CreateVmAsync", "ReuseVmAsync", "StopVmAsync", "DeleteVmAsync"],
            [typeof(IStorageGateway)] = ["EnsureBucketAsync", "UploadAsync"],
            [typeof(IProjectSetupGateway)] = ["EnableComputeApiAsync"],
            [typeof(IWorkerIdentityGateway)] = ["EnsureWorkerIdentityAsync"],
            [typeof(IServiceEnablementGateway)] = ["EnableServicesAsync"],
            [typeof(IBillingGateway)] = ["LinkProjectAsync"],
            [typeof(IProjectCatalogGateway)] = ["CreateProjectAsync"],
            [typeof(IQuotaGateway)] = [],
        };

        foreach (var (type, expected) in audited)
        {
            var mutating = type.GetMethods()
                .Select(m => m.Name)
                .Where(n => !readPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .Order()
                .ToArray();
            mutating.ShouldBe(expected.Order().ToArray(), type.Name);
        }
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
