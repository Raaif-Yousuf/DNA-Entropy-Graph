using DnaEntropyGraph.Core.Net;

namespace DnaEntropyGraph.Cloud.Net;

/// <summary>
/// An <see cref="HttpClient"/> over the same proxy-aware handler every Google client uses (issue #239), for a caller that is not a Google.Apis service
/// (the NCBI accession lookup, issue #684). A bare <c>new HttpClient()</c> would follow Windows only and ignore a typed proxy or "no proxy".
/// </summary>
public static class ProxiedHttpClient
{
    public static HttpClient Create(IProxyPolicy policy, long maxResponseContentBufferSize, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var client = new HttpClient(ProxyHttpClientFactory.NewHandler(new PolicyWebProxy(policy)), disposeHandler: true)
        {
            MaxResponseContentBufferSize = maxResponseContentBufferSize,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }
}
