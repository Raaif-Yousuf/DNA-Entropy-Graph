using DnaEntropyGraph.Core.Net;
using Velopack.Sources;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Velopack's HTTP downloader with the app's proxy policy in place of its own default handler (issue #239), so the update check and the
/// update download follow the same proxy choice as the Google clients: Windows (PAC, the Windows sign-in), a typed proxy, or none.
/// </summary>
public sealed class ProxiedFileDownloader(IProxyPolicy policy) : HttpClientFileDownloader
{
    private readonly PolicyWebProxy _proxy = new(policy);

    protected override HttpClientHandler CreateHttpClientHandler() => new()
    {
        Proxy = _proxy,
        UseProxy = true,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    };
}
