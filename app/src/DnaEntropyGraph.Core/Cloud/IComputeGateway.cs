namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Hard Rule 7: every Google Cloud call goes through an interface defined
/// HERE, in Core, never called directly from Presentation or App. Only
/// <c>DnaEntropyGraph.Cloud</c> may implement this against real
/// <c>Google.*</c> types; <c>FakeGcp</c> (also in Cloud) implements it
/// in-memory for every other test project. This project must never
/// reference <c>Google.*</c> itself.
///
/// <see cref="VmSpec"/> and <see cref="VmDescriptor"/> live in their own
/// file, <c>VmSpec.cs</c>, alongside this interface's namespace.
/// </summary>
public interface IComputeGateway
{
    /// <summary>
    /// Creates one VM in <paramref name="zone"/>. Takes the zone as its own
    /// parameter, not as part of <see cref="VmSpec"/>, because a zone is a
    /// per-attempt choice the caller (the zone ladder, issue #86) varies
    /// across retries for the *same* spec and the *same* VM name - the
    /// design this app relies on to try multiple zones under one job id
    /// (docs/cloud_design.md section 3). <see cref="VmSpec.EnsurePreconditions"/>
    /// runs before any request is built, real or fake, so a spec missing a
    /// label or a maxRunDuration - or one the real API would itself
    /// reject - never reaches a zone at all.
    /// </summary>
    Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken);

    Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken);
}
