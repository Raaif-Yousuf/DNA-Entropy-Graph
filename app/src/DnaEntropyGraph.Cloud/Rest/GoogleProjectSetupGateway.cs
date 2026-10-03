using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IProjectSetupGateway"/> (the preflight chain a run walks), built from the three real gateways
/// instead of a fourth set of Google calls: project state from Resource Manager, billing from Cloud Billing, the
/// Compute API from Service Usage. It is built over the raw gateways and wrapped once by
/// <see cref="ResilientProjectSetupGateway"/>, so a call is retried by one pipeline, not two.
/// </summary>
internal sealed class GoogleProjectSetupGateway : IProjectSetupGateway
{
    private readonly IProjectCatalogGateway _catalog;
    private readonly IBillingGateway _billing;
    private readonly IServiceEnablementGateway _services;

    public GoogleProjectSetupGateway(IProjectCatalogGateway catalog, IBillingGateway billing, IServiceEnablementGateway services)
    {
        _catalog = catalog;
        _billing = billing;
        _services = services;
    }

    public async Task<ProjectLifecycleState> GetProjectStateAsync(string projectId, CancellationToken cancellationToken)
        => (await _catalog.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false))?.State ?? ProjectLifecycleState.NotFound;

    public async Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken)
        => (await _billing.GetBillingStatusAsync(projectId, cancellationToken).ConfigureAwait(false)).Enabled;

    public Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken)
        => _services.IsServiceEnabledAsync(projectId, RequiredServices.Compute, cancellationToken);

    public Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken)
        => _services.EnableServicesAsync(projectId, [RequiredServices.Compute], cancellationToken);
}
