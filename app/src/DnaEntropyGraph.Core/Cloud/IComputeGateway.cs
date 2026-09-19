namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Hard Rule 7: every Google Cloud call goes through an interface defined
/// HERE, in Core, never called directly from Presentation or App. Only
/// <c>DnaEntropyGraph.Cloud</c> may implement this against real
/// <c>Google.*</c> types; <c>FakeGcp</c> (also in Cloud) implements it
/// in-memory for every other test project. This project must never
/// reference <c>Google.*</c> itself.
/// </summary>
public interface IComputeGateway
{
    Task<VmDescriptor> CreateVmAsync(VmSpec spec, CancellationToken cancellationToken);

    Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken);
}

/// <summary>
/// The request to create one VM. Hard Rule 10: every VM carries the
/// standard labels and a <c>maxRunDuration</c> - <c>VmSpec</c> is where that
/// precondition is meant to be enforced before a request is ever built
/// (the enforcing guard itself is issue #68's scope, not #61's).
/// </summary>
public sealed record VmSpec(
    string JobId,
    string InstallationId,
    string Model,
    string AppVersion,
    string MachineType,
    TimeSpan MaxRunDuration);

public sealed record VmDescriptor(string Name, string Zone, string Status);
