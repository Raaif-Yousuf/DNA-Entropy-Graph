using System.Net.Http.Headers;
using DnaEntropyGraph.Core.Cloud;
using Google.Apis.Http;
using Google.Apis.Services;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// How every real Google gateway is built: a REST discovery client whose HTTP stack can be replaced (a scripted handler
/// in tests) and whose every request carries the signed-in account's bearer token. Retry, backoff and the token refresh
/// are NOT done here: Google.Apis's own retry is left off so <see cref="CloudCallPipeline"/> is the only place a call
/// is retried (issue #258), once, with the policy the rest of the app uses.
/// </summary>
internal static class GoogleRestClient
{
    private const string ApplicationName = "DNA Entropy Graph";

    public static BaseClientService.Initializer CreateInitializer(IGcpAccessTokenSource tokens, GoogleCloudOptions options)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);

        return new BaseClientService.Initializer
        {
            ApplicationName = ApplicationName,
            HttpClientInitializer = new BearerTokenInitializer(tokens),
            HttpClientFactory = options.HttpHandler is { } handler ? new HandlerFactory(handler) : null,
            GZipEnabled = false,
            // Google.Apis retries a 503 by default; left on it would sleep and replay inside the pipeline's own retry.
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        };
    }

    /// <summary>
    /// A plain <see cref="HttpClient"/> for an API that has no discovery client (Cloud Quotas, issue #99): the same replaceable handler as the discovery clients and the
    /// same fresh bearer token on every request. Retry and token refresh stay in <see cref="CloudCallPipeline"/>.
    /// </summary>
    public static HttpClient CreateHttpClient(IGcpAccessTokenSource tokens, GoogleCloudOptions options)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        HttpMessageHandler inner = options.HttpHandler is { } handler ? new NonDisposingHandler(handler) : new SocketsHttpHandler();
        return new HttpClient(new BearerHandler(tokens, inner), disposeHandler: true);
    }

    private sealed class BearerHandler(IGcpAccessTokenSource tokens, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Puts <c>Authorization: Bearer &lt;token&gt;</c> on each request, reading the token fresh every time so a refreshed token is used by the replay.</summary>
    private sealed class BearerTokenInitializer(IGcpAccessTokenSource tokens) : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor
    {
        public void Initialize(ConfigurableHttpClient httpClient) => httpClient.MessageHandler.AddExecuteInterceptor(this);

        public async Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new NonDisposingHandler(handler);
    }

    /// <summary>One shared scripted handler serves many clients; none of them may dispose it.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            // Deliberately not disposing the shared inner handler.
        }
    }
}
