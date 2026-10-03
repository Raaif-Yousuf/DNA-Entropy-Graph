using System.Net;
using System.Text;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>One request the real gateway sent: what a wired-to-nothing gateway would get wrong.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body, string? ContentRange = null);

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

    public List<RecordedRequest> Requests { get; } = [];

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

    /// <summary>Queues a request that never answers: it ends only when the request's own token is cancelled (a hung connection).</summary>
    public ScriptedHttpHandler Hangs(HttpMethod method, string absolutePath)
        => Calls(method, absolutePath, async token =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    public IReadOnlyList<RecordedRequest> To(HttpMethod method, string absolutePath)
        => Requests.Where(r => r.Method == method && r.Uri.AbsolutePath == absolutePath).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_midBodyFailures.TryGetValue((request.Method.Method, request.RequestUri!.AbsolutePath), out var failures) && failures.Count > 0 && request.Content is not null)
        {
            var limit = failures.Dequeue();
            var partial = new byte[limit];
            var stream = await request.Content.ReadAsStreamAsync(cancellationToken);
            var read = await stream.ReadAtLeastAsync(partial, limit, throwOnEndOfStream: false, cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), Encoding.UTF8.GetString(partial, 0, read), request.Content.Headers.ContentRange?.ToString()));
            throw new HttpRequestException("The connection dropped while the request body was being sent.");
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body, request.Content?.Headers.ContentRange?.ToString()));

        if (_routes.TryGetValue((request.Method.Method, request.RequestUri!.AbsolutePath), out var queue) && queue.Count > 0)
        {
            return await queue.Dequeue()(cancellationToken);
        }

        return new HttpResponseMessage((HttpStatusCode)599)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":599,\"message\":\"unscripted request {request.Method} {request.RequestUri!.AbsolutePath}\"}}}}", Encoding.UTF8, "application/json"),
        };
    }
}
