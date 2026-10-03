using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IProjectSetupGateway"/> (the preflight chain a run walks), built from the three real gateways
/// instead of a fourth set of Google calls: project state from Resource Manager, billing from Cloud Billing, the
/// Compute API from Service Usage. It is NOT wrapped in <see cref="ResilientProjectSetupGateway"/>: the catalog and
/// service gateways it is built over already send each HTTP call through the pipeline themselves (so enabling Compute
/// retries its POST alone and never replays it when a poll read fails), and billing arrives already wrapped. A second
/// layer here would retry a retried call and could replay the POST.
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
