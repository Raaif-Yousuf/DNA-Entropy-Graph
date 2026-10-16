using System.Net;
using System.Security.Authentication;

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

    public static ProxyFailureKind? Detect(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
            {
                return ProxyFailureKind.CertificateUntrusted;
            }

            if (e is HttpRequestException http)
            {
                if (http.HttpRequestError == HttpRequestError.ProxyTunnelError)
                {
                    return ProxyFailureKind.ProxyRefused;
                }

                if (http.HttpRequestError == HttpRequestError.SecureConnectionError)
                {
                    return ProxyFailureKind.CertificateUntrusted;
                }

                if (http.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    return ProxyFailureKind.ProxyRefused;
                }
            }
        }

        return null;
    }

    /// <summary>407 on an API answer: a proxy asking for a sign-in, not Google refusing the caller.</summary>
    public static bool IsProxyAuthStatus(int? status) => status == 407;

    public static string CodeFor(ProxyFailureKind kind) => kind == ProxyFailureKind.ProxyRefused ? RefusedCode : CertificateCode;
}
