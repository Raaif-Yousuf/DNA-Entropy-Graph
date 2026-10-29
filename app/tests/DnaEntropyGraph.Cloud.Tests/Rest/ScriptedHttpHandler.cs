using System.Net;
using System.Text;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>One request the real gateway sent: what a wired-to-nothing gateway would get wrong.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body, string? ContentRange = null, string? UserProject = null);

/// <summary>
/// The HTTP-level fake for the real Google gateways (no GCP access: nothing here ever leaves the process). A route is a
/// method plus an absolute path; each <see cref="Returns(HttpMethod, string, int, string)"/> queues one response for
/// it, in order. A request nobody scripted fails the test loudly with a 599 and is recorded, so an unexpected call can
/// never read as success.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<(string Method, string Path), Queue<Func<CancellationToken, Task<HttpResponseMessage>>>> _routes = new();

    private readonly Dictionary<(string Method, string Path), Queue<int>> _midBodyFailures = new();

    private readonly Dictionary<(string Method, string Path), Func<int, string>> _fallbacks = new();

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    /// What a route answers once its queued responses are used up: <paramref name="body"/> gets the 0-based number of this fallback answer. A listing that
    /// never ends (the same page token for ever) needs this; without it the route would answer 599 after the queue and the test could not tell "stuck" from "unscripted".
    /// It stops after <see cref="FallbackLimit"/> answers (a 599 then), so a gateway with no guard fails a test instead of hanging it.
    /// </summary>
    public ScriptedHttpHandler Repeats(HttpMethod method, string absolutePath, Func<int, string> body)
    {
        _fallbacks[(method.Method, absolutePath)] = body;
        return this;
    }

    public const int FallbackLimit = 25;

    private readonly Dictionary<(string Method, string Path), int> _fallbackCalls = new();

    /// <summary>
    /// Queues a transport failure for the next unanswered request to this route: the handler reads only the first
    /// <paramref name="bytesRead"/> bytes of the request body, records them, then fails like a dropped connection
    /// (<see cref="HttpRequestException"/>). It takes precedence over a queued response and is consumed first.
    /// </summary>
    public ScriptedHttpHandler FailsMidBody(HttpMethod method, string absolutePath, int bytesRead)
    {
        var key = (method.Method, absolutePath);
        if (!_midBodyFailures.TryGetValue(key, out var queue))
        {
            _midBodyFailures[key] = queue = new Queue<int>();
        }

        queue.Enqueue(bytesRead);
        return this;
    }

    public ScriptedHttpHandler Returns(HttpMethod method, string absolutePath, int status, string json)
    {
        return Calls(method, absolutePath, _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
    }

    /// <summary>Queues a response the test computes: it can hang until the request is cancelled, throw, or cancel the caller mid-request.</summary>
    public ScriptedHttpHandler Calls(HttpMethod method, string absolutePath, Func<CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var key = (method.Method, absolutePath);
        if (!_routes.TryGetValue(key, out var queue))
        {
            _routes[key] = queue = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>();
        }

        queue.Enqueue(respond);
        return this;
    }

    /// <summary>Called as a request scripted by <see cref="Hangs"/> parks, before it waits: a test on a virtual clock moves it from here so the caller's deadline (armed before the request was sent) fires.</summary>
    public Action? OnHang { get; set; }

    /// <summary>Queues a request that never answers: it ends only when the request's own token is cancelled (a hung connection).</summary>
    public ScriptedHttpHandler Hangs(HttpMethod method, string absolutePath)
        => Calls(method, absolutePath, async token =>
        {
            OnHang?.Invoke();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    public IReadOnlyList<RecordedRequest> To(HttpMethod method, string absolutePath)
        => Requests.Where(r => r.Method == method && r.Uri.AbsolutePath == absolutePath).ToList();

    private static string? UserProject(HttpRequestMessage request) => request.Headers.TryGetValues("X-Goog-User-Project", out var values) ? values.Single() : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_midBodyFailures.TryGetValue((request.Method.Method, request.RequestUri!.AbsolutePath), out var failures) && failures.Count > 0 && request.Content is not null)
        {
            var limit = failures.Dequeue();
            var partial = new byte[limit];
            var stream = await request.Content.ReadAsStreamAsync(cancellationToken);
            var read = await stream.ReadAtLeastAsync(partial, limit, throwOnEndOfStream: false, cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), Encoding.UTF8.GetString(partial, 0, read), request.Content.Headers.ContentRange?.ToString(), UserProject(request)));
            throw new HttpRequestException("The connection dropped while the request body was being sent.");
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body, request.Content?.Headers.ContentRange?.ToString(), UserProject(request)));

        if (_routes.TryGetValue((request.Method.Method, request.RequestUri!.AbsolutePath), out var queue) && queue.Count > 0)
        {
            return await queue.Dequeue()(cancellationToken);
        }

        var routeKey = (request.Method.Method, request.RequestUri!.AbsolutePath);
        if (_fallbacks.TryGetValue(routeKey, out var fallback))
        {
            var n = _fallbackCalls.GetValueOrDefault(routeKey);
            _fallbackCalls[routeKey] = n + 1;
            if (n < FallbackLimit)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fallback(n), Encoding.UTF8, "application/json") };
            }
        }

        return new HttpResponseMessage((HttpStatusCode)599)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":599,\"message\":\"unscripted request {request.Method} {request.RequestUri!.AbsolutePath}\"}}}}", Encoding.UTF8, "application/json"),
        };
    }
}
