using System.Net;
using DnaEntropyGraph.Core.Net;
using Google.Apis.Http;

namespace DnaEntropyGraph.Cloud.Net;

/// <summary>
/// The one HTTP stack of every real Google client (issue #239): the sign-in's token exchange and revoke, and all the REST discovery
/// gateways (Resource Manager, Billing, Service Usage, Compute, Storage uploads and downloads, IAM). Each handler it makes sends every
/// request through a <see cref="PolicyWebProxy"/> over the live <see cref="IProxyPolicy"/>, so the proxy choice is applied here and
/// nowhere else, and a change in Settings reaches the next request of clients that already exist. None of these clients is gRPC (docs/cloud_design.md
/// section 16), so there is no second transport that could skip the policy.
/// </summary>
internal sealed class ProxyHttpClientFactory(IProxyPolicy policy) : HttpClientFactory
{
    private readonly PolicyWebProxy _proxy = new(policy);

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => NewHandler(_proxy);

    /// <summary>The handler for any client in the app: system proxy rules from the policy, no cookies, and a connection lifetime short enough that a changed proxy is picked up by new connections.</summary>
    internal static HttpClientHandler NewHandler(IWebProxy proxy) => new()
    {
        Proxy = proxy,
        UseProxy = true,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };
}
