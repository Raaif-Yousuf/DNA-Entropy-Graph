using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #105: the resilience wrappers' policy for the Cloud page's methods. Cleanup (delete a disk, delete a prefix, list what is
/// ours) must still run with the offline breaker open, exactly as stop and delete of a VM do, because it is what stops the meter;
/// a start is new work and fails fast while offline.
/// </summary>
public class ResilientCloudPageTests
{
    private const string Zone = "us-central1-a";

    private sealed class ManualTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => 0;

        public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    }

    private static (FakeGcp Gcp, ResilientComputeGateway Compute, ResilientStorageGateway Storage, CloudCallPipeline Pipeline) OpenBreakerRig()
    {
        var gcp = new FakeGcp().WithTransientFailures(503, 2);
        var pipeline = new CloudCallPipeline(
            new CloudRetryOptions
            {
                MaxRetryAttempts = 1,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                TimeProvider = new ManualTimeProvider(),
                BreakerMinimumThroughput = 2,
                BreakerFailureRatio = 0.5,
                BreakerSamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
            },
            gcp,
            new CloudRetryLog());
        return (gcp, new ResilientComputeGateway(gcp, pipeline), new ResilientStorageGateway(gcp, pipeline), pipeline);
    }

    [Fact]
    public async Task With_the_breaker_open_cleanup_calls_still_reach_the_cloud_and_a_start_fails_fast()
    {
        var (gcp, compute, storage, pipeline) = OpenBreakerRig();
        gcp.WithDisk(new DiskDescriptor("d1", Zone, 150, "READY", new Dictionary<string, string> { ["app"] = VmSpec.AppLabelValue, ["installation-id"] = "i1" }));
        gcp.PutObject("b", "jobs/j1/manifest.json", [1]);
        await Should.ThrowAsync<CloudOperationException>(() => compute.GetVmAsync("v", Zone, CancellationToken.None));
        pipeline.IsOffline.ShouldBeTrue();

        (await compute.ListDisksByInstallationAsync("i1", CancellationToken.None)).Count.ShouldBe(1);
        await compute.DeleteDiskAsync("d1", Zone, CancellationToken.None);
        (await storage.DeletePrefixAsync("b", "jobs/j1/", CancellationToken.None)).ShouldBe(1);

        gcp.Disks.ShouldBeEmpty();
        var ex = await Should.ThrowAsync<CloudOperationException>(() => compute.StartVmAsync("v", Zone, CancellationToken.None));
        ex.Kind.ShouldBe(CloudErrorKind.Network);
        ex.Error.Code.ShouldNotBe("VM_NOT_FOUND", "the start must have been refused by the breaker before it reached the fake");
    }
}
