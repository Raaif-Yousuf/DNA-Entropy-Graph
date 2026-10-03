namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// First-run wizard step 5 (issue #52): the Google services the app needs must be on in the user's project, and
/// turning Compute on also creates the project's default network. See IComputeGateway for the Hard Rule 7 boundary.
/// Enabling is a long-running operation: the real error is in the polled operation, and a finished operation is not
/// the same as every service reading ENABLED, so <see cref="EnableServicesAsync"/> returns only once each one does.
/// A caller who is a member of the project but not its Owner gets <see cref="SetupErrorCodes.NotProjectOwner"/>.
/// </summary>
public interface IServiceEnablementGateway
{
    Task<bool> IsServiceEnabledAsync(string projectId, string serviceId, CancellationToken cancellationToken);

    /// <summary>Turns the services on (idempotent) and waits until every one is ENABLED.</summary>
    Task EnableServicesAsync(string projectId, IReadOnlyList<string> serviceIds, CancellationToken cancellationToken);
}

/// <summary>The services the app needs in the user's project, as Google names them.</summary>
public static class RequiredServices
{
    public const string Compute = "compute.googleapis.com";
    public const string Storage = "storage.googleapis.com";
    public const string CloudQuotas = "cloudquotas.googleapis.com";

    /// <summary>Compute first: it is the one a stockout-shaped failure hides behind (CLAUDE.md Critical Pitfalls).</summary>
    public static IReadOnlyList<string> Ids { get; } = [Compute, Storage, CloudQuotas];
}
