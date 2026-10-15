using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #691: whether a lost VM hit its run limit is judged on the PLATFORM's clock. The laptop's wall clock is wrong by an unknown skew, so a
/// laptop an hour fast called a VM lost for another reason "run_time_limit" and one an hour slow missed a real limit. The skew is estimated from
/// the server's own time on the last look that found the VM (the HTTP Date header on the real gateway, <see cref="VmDescriptor.ServerNow"/>).
/// </summary>
public class VmLostClockSkewTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-02T08:00:00Z");

    /// <summary>The app's clock: the platform's time plus a constant skew. Elapsed time follows the platform's clock, as a real monotonic clock does.</summary>
    private sealed class SkewedTime(ManualTimeProvider server, TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => server.GetUtcNow() + skew;

        public override long TimestampFrequency => server.TimestampFrequency;

        public override long GetTimestamp() => server.GetTimestamp();
    }

    private sealed class HookedCompute(FakeGcp inner) : IComputeGateway
    {
        public Func<string, string, CancellationToken, Task<VmDescriptor?>>? OnGet { get; set; }

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
            => OnGet is null ? inner.GetVmAsync(vmName, zone, cancellationToken) : OnGet(vmName, zone, cancellationToken);

        public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StopVmAsync(vmName, zone, cancellationToken);

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken) => inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }

    private static VmSpec Spec(string jobId) => new(
        ProjectId: Project, InstallationId: "install-1", JobId: jobId, Model: "evo2_7b", AppVersion: "0.1.0", Lifecycle: "stop",
        MachineType: "g2-standard-8", MaxRunDuration: TimeSpan.FromMinutes(20), TerminationAction: "DELETE");

    /// <summary>
    /// Runs a job whose VM is gone at the third look with no result. <paramref name="serverMinutesBeforeLoss"/> is how much PLATFORM time passes first
    /// (the platform deletes the VM at its limit when that is 30, and the VM is merely lost when it is 1).
    /// </summary>
    private static async Task<RunFailureOutcome> LostAsync(string jobId, TimeSpan laptopSkew, int serverMinutesBeforeLoss, bool adopted)
    {
        var server = new ManualTimeProvider(Start);
        var gcp = new FakeGcp(server).WithWorker(FakeWorkerMode.Never).WithMaxRunDurationEnforced();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request(jobId, Spec(jobId));
        if (adopted)
        {
            await gcp.CreateVmAsync(request.Spec, Zone, CancellationToken.None);
            await repo.UpsertAsync(new RunRecord(jobId, JobPhase.Running, Start.UtcDateTime), CancellationToken.None);
            gcp.PutObject(FakeGcp.BucketName(Project), $"jobs/{jobId}/manifest.json", """{"inputs":[{"name":"seq.gb"}]}"""u8.ToArray());
        }

        var looks = 0;
        var compute = new HookedCompute(gcp)
        {
            OnGet = async (name, zone, token) =>
            {
                if (++looks == 3)
                {
                    server.Advance(TimeSpan.FromMinutes(serverMinutesBeforeLoss));
                    await gcp.DeleteVmAsync(name, zone, token); // gone either way: the platform at its limit, or lost for another reason
                }

                return await gcp.GetVmAsync(name, zone, token);
            },
        };
        var runner = new CloudJobRunner(compute, gcp, gcp, gcp, gcp, repo)
        {
            TimeProvider = new SkewedTime(server, laptopSkew),
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            LifecycleTimeout = TimeSpan.FromSeconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = await runner.RunAsync(request, CancellationToken.None);
        return new RunFailureOutcome(result.FailureCode, result.FailureMessage);
    }

    private sealed record RunFailureOutcome(string? Code, string? Message);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_laptop_an_hour_fast_does_not_call_a_VM_lost_after_a_minute_run_time_limit(bool adopted)
    {
        var outcome = await LostAsync("job-fast", TimeSpan.FromHours(1), serverMinutesBeforeLoss: 1, adopted);

        outcome.Code.ShouldBe(RunErrorCodes.VmUnhealthy, outcome.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_laptop_an_hour_slow_still_records_run_time_limit_for_a_VM_the_platform_deleted_at_its_limit(bool adopted)
    {
        var outcome = await LostAsync("job-slow", TimeSpan.FromHours(-1), serverMinutesBeforeLoss: 30, adopted);

        outcome.Code.ShouldBe(RunErrorCodes.RunTimeLimit, outcome.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_correct_clock_records_run_time_limit_at_the_limit_and_vm_unhealthy_before_it(bool adopted)
    {
        (await LostAsync("job-ok-late", TimeSpan.Zero, serverMinutesBeforeLoss: 30, adopted)).Code.ShouldBe(RunErrorCodes.RunTimeLimit);
        (await LostAsync("job-ok-early", TimeSpan.Zero, serverMinutesBeforeLoss: 1, adopted)).Code.ShouldBe(RunErrorCodes.VmUnhealthy);
    }
}
