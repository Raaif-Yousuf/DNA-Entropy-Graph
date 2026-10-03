using DnaEntropyGraph.Core.Cloud;
using Google.Apis.Cloudbilling.v1;
using Google.Apis.CloudResourceManager.v3;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>The real, Google-backed gateways, each already wrapped in the resilience pipeline.</summary>
public sealed record GoogleCloudGatewaySet(IProjectCatalogGateway ProjectCatalog, IBillingGateway Billing);

/// <summary>
/// The one place the real gateways are built, and so the one place production switches from <see cref="FakeGcp"/> to
/// Google (docs/cloud_design.md section 16): <c>ServiceRegistration</c> calls this with the app's
/// <see cref="IGcpAccessTokenSource"/> and the shared <see cref="CloudCallPipeline"/> when the whole preflight chain
/// has a real implementation (issue #56) and the placeholder project id is gone (issue #520). Tests call it the same
/// way, with a scripted HTTP handler in <see cref="GoogleCloudOptions.HttpHandler"/>.
/// </summary>
public static class GoogleCloudGateways
{
    public static GoogleCloudGatewaySet Create(IGcpAccessTokenSource tokens, CloudCallPipeline pipeline, GoogleCloudOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(pipeline);
        options ??= new GoogleCloudOptions();

        var resourceManager = new CloudResourceManagerService(GoogleRestClient.CreateInitializer(tokens, options));
        var billing = new CloudbillingService(GoogleRestClient.CreateInitializer(tokens, options));
        return new GoogleCloudGatewaySet(
            new ResilientProjectCatalogGateway(new GoogleProjectCatalogGateway(resourceManager, options), pipeline),
            new ResilientBillingGateway(new GoogleBillingGateway(billing), pipeline));
    }
}
