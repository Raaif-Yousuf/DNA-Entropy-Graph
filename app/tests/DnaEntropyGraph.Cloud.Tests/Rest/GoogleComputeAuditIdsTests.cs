using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cloud.Audit;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #245 review: the request id is minted by the client, so it is known before the insert leaves. A create that fails (403 on
/// instances.insert) must still leave the id in the audit log, and the started line must carry it, or support cannot find the call.
/// </summary>
public class GoogleComputeAuditIdsTests
{
    private const string Instances = "/compute/v1/projects/my-lab/zones/us-central1-a/instances";

    private sealed class MemoryLog : ICloudAuditLog
    {
        public List<CloudAuditEntry> All { get; } = [];

        public void Record(CloudAuditEntry entry) => All.Add(entry);
    }

    private static VmSpec Spec() => new(
        ProjectId: "my-lab",
        InstallationId: "install-1",
        JobId: "job1",
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromMinutes(90),
        TerminationAction: "DELETE")
    {
        ServiceAccountEmail = "worker-sa@my-lab.iam.gserviceaccount.com",
        Metadata = new Dictionary<string, string> { ["startup-script"] = "#!/usr/bin/env bash\n", ["deg-job-id"] = "job1" },
    };

    [Fact]
    public async Task A_create_refused_with_403_leaves_the_client_minted_request_id_on_both_the_started_and_the_finished_line()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(
            HttpMethod.Post,
            Instances,
            403,
            "{\"error\":{\"code\":403,\"message\":\"Required 'compute.instances.create' permission\",\"status\":\"PERMISSION_DENIED\"}}");
        var log = new MemoryLog();
        var audited = new AuditedComputeGateway(rig.Gateways.Compute, new CloudAuditor(log, () => "install-1", TimeProvider.System));

        await Should.ThrowAsync<CloudOperationException>(() => audited.CreateVmAsync(Spec(), "us-central1-a", CancellationToken.None));

        var sent = rig.Handler.To(HttpMethod.Post, Instances).First().Uri.Query;
        var requestId = System.Web.HttpUtility.ParseQueryString(sent)["requestId"];
        requestId.ShouldNotBeNullOrEmpty("the insert carries the id: it is what Google's own log is searched by");
        var started = log.All.Single(e => e.Phase == "started");
        var finished = log.All.Single(e => e.Phase == "finished");
        started.RequestId.ShouldBe(requestId);
        finished.RequestId.ShouldBe(requestId);
        finished.Outcome.ShouldBe("permission");
        finished.OperationId.ShouldBeNull("Google never answered with an operation");
    }
}
