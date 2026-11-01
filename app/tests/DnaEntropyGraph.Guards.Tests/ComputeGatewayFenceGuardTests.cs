using System.Reflection;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #476 (round 3), wired-to-nothing: <see cref="IComputeGateway.MarkEndingAsync"/> is the fence before the app ends a Keep-alive VM. Its default body throws, so a
/// gateway that forgot to implement it fails loudly at the first sweep instead of ending a VM unfenced; this guard makes the same mistake a red build. Every
/// non-abstract class in app/src that implements <see cref="IComputeGateway"/> (a real gateway, the fake, a decorator) must DECLARE the method itself.
/// Decorators are also covered by <see cref="DecoratorMembersGuardTests"/>; the real and fake gateways are not decorators, which is the gap this closes.
/// </summary>
public class ComputeGatewayFenceGuardTests
{
    internal static IEnumerable<Type> Offenders(IEnumerable<Type> types)
        => types.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IComputeGateway).IsAssignableFrom(t) && !Declares(t));

    private static bool Declares(Type t)
    {
        var map = t.GetInterfaceMap(typeof(IComputeGateway));
        var i = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IComputeGateway.MarkEndingAsync));
        return i >= 0 && !map.TargetMethods[i].DeclaringType!.IsInterface;
    }

    public sealed class Declaring : NoOpGateway, IComputeGateway
    {
        public Task<VmDescriptor?> MarkEndingAsync(CloudScope scope, string vmName, string zone, string jobId, CancellationToken cancellationToken) => Task.FromResult<VmDescriptor?>(null);
    }

    public sealed class Forgetting : NoOpGateway;

    public abstract class NoOpGateway : IComputeGateway
    {
        public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void A_gateway_that_relies_on_the_default_body_is_reported_and_one_that_declares_it_is_not()
    {
        Offenders([typeof(Forgetting), typeof(Declaring)]).ShouldBe([typeof(Forgetting)]);
    }

    [Fact]
    public void The_scan_sees_the_real_gateway_the_fake_and_both_decorators()
    {
        var implementers = DecoratorMembersGuardTests.AppSrcAssemblies().SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IComputeGateway).IsAssignableFrom(t))
            .Select(t => t.Name).ToHashSet();
        foreach (var known in new[] { "GoogleComputeGateway", "FakeGcp", "ResilientComputeGateway", "AuditedComputeGateway" })
        {
            implementers.ShouldContain(known, "the scan lost a gateway it must cover (vacuity)");
        }
    }

    [Fact]
    public void Every_gateway_in_app_src_declares_the_keep_alive_fence_itself()
    {
        var offenders = Offenders(DecoratorMembersGuardTests.AppSrcAssemblies().SelectMany(a => a.GetTypes())).Select(t => t.FullName).ToList();
        offenders.ShouldBeEmpty("Each IComputeGateway implementation must declare MarkEndingAsync (the interface's default throws); a decorator must forward it.\n" + string.Join("\n", offenders));
    }
}
