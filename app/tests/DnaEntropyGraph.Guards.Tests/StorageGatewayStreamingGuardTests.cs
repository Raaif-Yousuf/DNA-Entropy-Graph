using System.Reflection;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #588: <see cref="IStorageGateway.TryDownloadToFileAsync"/> has a default implementation that buffers the whole object through
/// <see cref="IStorageGateway.TryDownloadAsync"/>. A new gateway that silently inherited it would bring the memory problem back, so every
/// implementer shipped in the Cloud assembly must declare its own.
/// </summary>
public class StorageGatewayStreamingGuardTests
{
    private static IReadOnlyList<Type> Implementers()
        => typeof(FakeGcp).Assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IStorageGateway).IsAssignableFrom(t)).ToList();

    [Fact]
    public void Every_storage_gateway_in_the_cloud_assembly_declares_its_own_TryDownloadToFileAsync()
    {
        var implementers = Implementers();
        implementers.Count.ShouldBeGreaterThanOrEqualTo(3, "the scan must see FakeGcp, ResilientStorageGateway and GoogleStorageGateway");

        var inherited = implementers
            .Where(t => !t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(m => m.Name == nameof(IStorageGateway.TryDownloadToFileAsync) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(CloudScope))) // the scoped overload (#830): FakeGcp also keeps an unscoped one
            .Select(t => t.Name)
            .ToList();

        inherited.ShouldBeEmpty("these gateways inherit the buffering default of TryDownloadToFileAsync: " + string.Join(", ", inherited));
    }
}
