namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The first-run wizard's preflight chain (docs/architecture.md Critical
/// Pitfalls: billing off and API off look exactly like a stockout).
/// See IComputeGateway for the Hard Rule 7 boundary this interface exists to enforce.
/// </summary>
public interface IProjectSetupGateway
{
    Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken);

    Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken);

    Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken);
}
