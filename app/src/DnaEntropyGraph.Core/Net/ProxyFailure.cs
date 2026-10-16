using System.Net;

namespace DnaEntropyGraph.Core.Net;

/// <summary>What kind of proxy-shaped failure a connection error is.</summary>
public enum ProxyFailureKind
{
    /// <summary>The proxy answered the tunnel request with a refusal (407 sign-in needed, 403, 502, ...).</summary>
    ProxyRefused,

    /// <summary>The proxy or a security program on the network presented a certificate this PC does not trust (TLS inspection).</summary>
    CertificateUntrusted,
}

/// <summary>Recognises proxy-caused failures from the exception itself, by the typed error, never by message text (issue #239).</summary>
public static class ProxyFailure
{
    /// <summary>The <c>CloudError.Code</c> a proxy refusal is recorded under.</summary>
    public const string RefusedCode = "PROXY_REFUSED";

    /// <summary>The <c>CloudError.Code</c> an untrusted-certificate failure is recorded under.</summary>
    public const string CertificateCode = "NETWORK_CERTIFICATE";

    /// <summary>
    /// The <see cref="Exception.Data"/> key the app's own HTTP handler sets on a failed request when the TLS handshake did reach a certificate and
    /// the chain was proven untrusted (an unknown root or a partial chain). Without it a handshake failure is NOT called a certificate problem:
    /// an end-of-stream or reset during the handshake, a captive portal and a wrong clock raise the same exception types.
    /// </summary>
    public const string UntrustedChainKey = "deg.untrusted_chain";

    public static ProxyFailureKind? Detect(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e.Data.Contains(UntrustedChainKey))
            {
                return ProxyFailureKind.CertificateUntrusted;
            }

            if (e is not HttpRequestException http)
            {
                continue;
            }

            // Only a sign-in demand (407) or a ban (403) from the proxy is a refusal the user can act on. A 502, 503, 504 or a dropped tunnel is a
            // proxy that is unwell right now: a transient network failure that is retried like any other.
            if (http.StatusCode == HttpStatusCode.ProxyAuthenticationRequired
                || (http.HttpRequestError == HttpRequestError.ProxyTunnelError && http.StatusCode == HttpStatusCode.Forbidden))
            {
                return ProxyFailureKind.ProxyRefused;
            }
        }

        return null;
    }
    /// <summary>407 on an API answer: a proxy asking for a sign-in, not Google refusing the caller.</summary>
    public static bool IsProxyAuthStatus(int? status) => status == 407;

    public static string CodeFor(ProxyFailureKind kind) => kind == ProxyFailureKind.ProxyRefused ? RefusedCode : CertificateCode;
}
