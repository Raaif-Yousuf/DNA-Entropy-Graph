using DnaEntropyGraph.Core.Cloud;
using Google.Apis.Cloudbilling.v1;
using Google.Apis.CloudResourceManager.v3;
using Google.Apis.Compute.v1;
using Google.Apis.ServiceUsage.v1;
using Google.Apis.Storage.v1;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real, Google-backed gateways, each already wrapped in the resilience pipeline. <see cref="ProjectSetup"/> is
/// the preflight chain (<see cref="IProjectSetupGateway"/>) that <c>CloudJobRunner</c> consumes; the next three are
/// the wizard's steps 3 to 5, and <see cref="Storage"/> is the results bucket and the object calls (issue #53).
/// </summary>
public sealed record GoogleCloudGatewaySet(
    IProjectCatalogGateway ProjectCatalog,
    IBillingGateway Billing,
    IServiceEnablementGateway Services,
    IProjectSetupGateway ProjectSetup,
    IStorageGateway Storage,
    IComputeGateway Compute);

/// <summary>
/// The one place the real gateways are built, and so the one place production switches from <see cref="FakeGcp"/> to
/// Google (docs/cloud_design.md section 16): <c>ServiceRegistration</c> calls this with the app's
/// <see cref="IGcpAccessTokenSource"/> and the shared <see cref="CloudCallPipeline"/> when the whole preflight chain
/// has a real implementation (issue #56) and the placeholder project id is gone (issue #520). Tests call it the same
/// way, with a scripted HTTP handler in <see cref="GoogleCloudOptions.HttpHandler"/>.
/// </summary>
public static class GoogleCloudGateways
{
    public static GoogleCloudGatewaySet Create(IGcpAccessTokenSource tokens, CloudCallPipeline pipeline, GoogleCloudOptions? options = null, Func<string?>? selectedProjectId = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(pipeline);
        options ??= new GoogleCloudOptions();

        var catalog = new GoogleProjectCatalogGateway(new CloudResourceManagerService(GoogleRestClient.CreateInitializer(tokens, options)), pipeline, options);
        var billing = new ResilientBillingGateway(new GoogleBillingGateway(new CloudbillingService(GoogleRestClient.CreateInitializer(tokens, options))), pipeline);
        var services = new GoogleServiceUsageGateway(new ServiceUsageService(GoogleRestClient.CreateInitializer(tokens, options)), pipeline, options);

        var storage = new GoogleStorageGateway(new StorageService(GoogleRestClient.CreateInitializer(tokens, options)), catalog, pipeline, options);

        return new GoogleCloudGatewaySet(
            // Not wrapped: the project catalog and the service-enablement gateway route each of their own HTTP calls through
            // the pipeline (a create or enable is a POST plus polled reads, and one retry around the whole thing would
            // re-POST when a read fails). Billing is single-call, so it is wrapped whole.
            catalog,
            billing,
            services,
            new GoogleProjectSetupGateway(catalog, billing, services),
            // Not wrapped: EnsureBucketAsync is a list, a create, a read-back and a patch, and a whole-method retry would
            // replay the create. Each of its HTTP calls goes through the pipeline itself.
            storage,
            // Wrapped whole, like every IComputeGateway (a pipeline retry of an insert is safe: instance names are unique per zone).
            new ResilientComputeGateway(
                new GoogleComputeGateway(new ComputeService(GoogleRestClient.CreateInitializer(tokens, options)), options, selectedProjectId ?? (() => null)),
                pipeline));
    }
}
