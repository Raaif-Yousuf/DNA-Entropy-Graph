using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
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

    /// <summary>
    /// The handler for a Google client. Redirects, cookies and decompression are exactly Google.Apis's own defaults (MEASURED 2026-10-16 against
    /// <c>HttpClientFactory.CreateHandler</c> in Google.Apis 1.77.0: no auto-redirect, because its message handler follows redirects itself, cookies on,
    /// no decompression, because it reads gzip itself), and a pooled connection ends after ten minutes so a changed proxy is not held open forever.
    /// Certificate validation is the default one: the callback only RECORDS why a chain was refused and still refuses it.
    /// </summary>
    internal static HttpMessageHandler NewHandler(IWebProxy proxy) => NewHandler(proxy, new UntrustedChainEvidence());

    internal static HttpMessageHandler NewHandler(IWebProxy proxy, UntrustedChainEvidence evidence)
    {
        var sockets = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = evidence.Validate },
        };
        return new UntrustedChainTagHandler(sockets, evidence);
    }

    /// <summary>
    /// Remembers, per destination host and briefly, WHEN a handshake was refused for an unknown root or a partial chain. The validation callback runs on the
    /// connection, not on the request (its sender is the SslStream, MEASURED 2026-10-16), so the host name is the only thing the two share. Nothing is consumed:
    /// a request counts evidence only when it was written after that request began (see <see cref="SeenSince"/>), so concurrent requests all see it, and
    /// evidence left behind by a cancelled request is older than any later request.
    /// </summary>
    internal sealed class UntrustedChainEvidence
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);

        public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (sender is SslStream { TargetHostName: { Length: > 0 } host }
                && errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)
                && chain is not null
                && chain.ChainStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.UntrustedRoot) || s.Status.HasFlag(X509ChainStatusFlags.PartialChain)))
            {
                Record(host);
            }

            return false;
        }

        public void Record(string host)
        {
            var now = Stopwatch.GetTimestamp();
            _seen[host] = now;
            foreach (var (key, at) in _seen)
            {
                if (Stopwatch.GetElapsedTime(at, now) > Window)
                {
                    _seen.TryRemove(key, out _);
                }
            }
        }

        public bool SeenSince(string host, long requestStart)
            => _seen.TryGetValue(host, out var at) && at >= requestStart && Stopwatch.GetElapsedTime(at) <= Window;
    }

    /// <summary>
    /// Marks a failed request's exception when its TLS handshake was refused for an unknown root or a partial chain, so <see cref="ProxyFailure"/> can tell that
    /// from a dropped handshake. Only a TLS failure is ever tagged: a proxy refusal or a connection error raised while another request's evidence is on file stays what it is.
    /// </summary>
    internal sealed class UntrustedChainTagHandler(HttpMessageHandler inner, UntrustedChainEvidence evidence) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (IsTlsFailure(ex) && request.RequestUri is { } uri && evidence.SeenSince(uri.Host, started))
            {
                ex.Data[ProxyFailure.UntrustedChainKey] = true;
                throw;
            }
        }

        private static bool IsTlsFailure(HttpRequestException ex)
            => ex.HttpRequestError == HttpRequestError.SecureConnectionError || ex.InnerException is System.Security.Authentication.AuthenticationException;
    }
}
