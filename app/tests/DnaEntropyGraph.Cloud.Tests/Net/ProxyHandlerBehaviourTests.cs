using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DnaEntropyGraph.Cloud.Net;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Cloud.Tests.Rest;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Net;
using Google.Apis.Http;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Net;

/// <summary>Issue #239 cold review: what the handler does that Google's own does (redirects, cookies), what a changed proxy password does to a live client, and which handshake failures count as a certificate problem.</summary>
public class ProxyHandlerBehaviourTests
{
    private sealed class GoogleDefaults : HttpClientFactory
    {
        public HttpMessageHandler Default() => CreateHandler(new CreateHttpClientArgs());
    }

    /// <summary>A loopback TLS server with a self-signed certificate no machine trusts, or one that hangs up straight after the connection opens.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2? _certificate;

        public TlsServer(bool hangUp)
        {
            if (!hangUp)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder();
                names.AddIpAddress(IPAddress.Loopback);
                names.AddDnsName("localhost");
                request.CertificateExtensions.Add(names.Build());
                using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            }

            _listener.Start();
            _ = Task.Run(() => ServeAsync(hangUp));
        }

        public string Url => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/probe";

        private async Task ServeAsync(bool hangUp)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    if (hangUp)
                    {
                        continue; // disposing the socket straight away is the end-of-stream during the handshake
                    }

                    try
                    {
                        using var ssl = new SslStream(client.GetStream(), false);
                        await ssl.AuthenticateAsServerAsync(_certificate!);
                    }
                    catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _certificate?.Dispose();
            _stop.Dispose();
        }
    }

    private static HttpClient NewClient(ProxyPolicy? policy = null) => new ProxyHttpClientFactory(policy ?? ProxyHttpClientFactoryTests.NewPolicy()).CreateHttpClient(new CreateHttpClientArgs());

    private static async Task<Exception> FailureOf(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("the call was expected to fail");
    }

    private static HttpMessageHandler Innermost(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler d)
        {
            handler = d.InnerHandler!;
        }

        return handler;
    }

    [Fact]
    public void The_handler_matches_the_redirect_cookie_and_decompression_behaviour_of_Googles_own_default()
    {
        var google = Innermost(new GoogleDefaults().Default());
        var ours = Innermost(ProxyHttpClientFactory.NewHandler(new PolicyWebProxy(ProxyPolicy.SystemDefault)));

        (bool Redirect, bool Cookies, DecompressionMethods Decompression) Read(HttpMessageHandler h) => h switch
        {
            HttpClientHandler c => (c.AllowAutoRedirect, c.UseCookies, c.AutomaticDecompression),
            SocketsHttpHandler s => (s.AllowAutoRedirect, s.UseCookies, s.AutomaticDecompression),
            _ => throw new InvalidOperationException(h.GetType().FullName),
        };

        Read(ours).ShouldBe(Read(google));
    }

    [Fact]
    public void The_handler_ends_a_pooled_connection_in_minutes_so_a_changed_proxy_is_not_held_open_forever()
    {
        var inner = Innermost(ProxyHttpClientFactory.NewHandler(new PolicyWebProxy(ProxyPolicy.SystemDefault))).ShouldBeOfType<SocketsHttpHandler>();

        inner.PooledConnectionLifetime.ShouldBeGreaterThan(TimeSpan.Zero);
        inner.PooledConnectionLifetime.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task A_proxy_password_changed_in_settings_is_what_the_next_request_sends_on_a_client_that_already_exists()
    {
        using var proxy = new RefusingProxy(407);
        var policy = ProxyHttpClientFactoryTests.NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), new NetworkCredential("alice", "first"));
        using var client = NewClient(policy);
        await FailureOf(() => client.GetAsync("https://storage.googleapis.com/one"));

        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), new NetworkCredential("alice", "second"));
        await FailureOf(() => client.GetAsync("https://storage.googleapis.com/two"));

        string Header(string pw) => "Proxy-Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("alice:" + pw));
        var last = proxy.Requests.Last();
        last.ShouldContain(Header("second"));
        last.ShouldNotContain(Header("first"));
    }

    [Fact]
    public async Task A_certificate_nobody_trusts_is_marked_as_an_untrusted_chain()
    {
        using var server = new TlsServer(hangUp: false);
        using var client = NewClient();

        var failure = await FailureOf(() => client.GetAsync(server.Url));

        ProxyFailure.Detect(failure).ShouldBe(ProxyFailureKind.CertificateUntrusted);
    }

    [Fact]
    public async Task A_connection_that_is_dropped_during_the_handshake_is_not_called_a_certificate_problem()
    {
        using var server = new TlsServer(hangUp: true);
        using var client = NewClient();

        var failure = await FailureOf(() => client.GetAsync(server.Url));

        failure.ShouldBeOfType<HttpRequestException>();
        ProxyFailure.Detect(failure).ShouldBeNull();
    }

    [Fact]
    public async Task A_proxy_answering_503_is_retried_like_any_network_failure_and_is_not_a_proxy_refusal()
    {
        using var proxy = new RefusingProxy(503);
        var policy = ProxyHttpClientFactoryTests.NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);
        using var client = NewClient(policy);
        var pipeline = new CloudCallPipeline(
            new CloudRetryOptions { MaxRetryAttempts = 3, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
            new StubTokenSource(),
            new CloudRetryLog());

        var failure = await Should.ThrowAsync<CloudOperationException>(() => pipeline.ExecuteAsync<HttpResponseMessage>(
            "test.call",
            ct => client.GetAsync("https://storage.googleapis.com/probe", ct),
            TestContext.Current.CancellationToken));

        proxy.Connects.Count().ShouldBe(4);
        failure.Error.Code.ShouldBeNull();
        RunErrorCodes.For(failure.Error, failure.Kind).ShouldBe("network");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    [Fact]
    public async Task Two_parallel_requests_to_one_untrusted_host_are_both_called_certificate_problems()
    {
        using var server = new TlsServer(hangUp: false);
        using var client = NewClient();

        var both = await Task.WhenAll(
            FailureOf(() => client.GetAsync(server.Url)),
            FailureOf(() => client.GetAsync(server.Url)),
            FailureOf(() => client.GetAsync(server.Url)));

        both.ShouldAllBe(f => ProxyFailure.Detect(f) == ProxyFailureKind.CertificateUntrusted);
    }

    [Fact]
    public async Task Evidence_left_by_an_earlier_request_that_was_cancelled_never_labels_a_later_plain_connection_failure()
    {
        using var server = new TlsServer(hangUp: true);
        var evidence = new ProxyHttpClientFactory.UntrustedChainEvidence();
        evidence.Record("127.0.0.1"); // the handshake of a request that was then cancelled
        using var client = new HttpClient(ProxyHttpClientFactory.NewHandler(new PolicyWebProxy(ProxyPolicy.SystemDefault), evidence));

        var failure = await FailureOf(() => client.GetAsync(server.Url));

        ProxyFailure.Detect(failure).ShouldBeNull();
    }

    [Fact]
    public async Task A_proxy_refusal_or_a_dropped_connection_is_never_relabelled_by_evidence_from_another_request()
    {
        var evidence = new ProxyHttpClientFactory.UntrustedChainEvidence();
        var refusal = new StubHandler(_ =>
        {
            evidence.Record("storage.googleapis.com"); // another request's handshake, written while this one is in flight
            throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "refused", null, HttpStatusCode.ProxyAuthenticationRequired);
        });
        var dropped = new StubHandler(_ =>
        {
            evidence.Record("storage.googleapis.com");
            throw new HttpRequestException(HttpRequestError.ConnectionError, "reset");
        });

        var refused = await FailureOf(() => new HttpClient(new ProxyHttpClientFactory.UntrustedChainTagHandler(refusal, evidence)).GetAsync("https://storage.googleapis.com/a"));
        var reset = await FailureOf(() => new HttpClient(new ProxyHttpClientFactory.UntrustedChainTagHandler(dropped, evidence)).GetAsync("https://storage.googleapis.com/a"));

        ProxyFailure.Detect(refused).ShouldBe(ProxyFailureKind.ProxyRefused);
        ProxyFailure.Detect(reset).ShouldBeNull();
    }
}
