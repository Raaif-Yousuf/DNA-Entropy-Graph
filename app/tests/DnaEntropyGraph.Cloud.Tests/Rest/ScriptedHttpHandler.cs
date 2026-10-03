using System.Net;
using System.Text;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>One request the real gateway sent: what a wired-to-nothing gateway would get wrong.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

/// <summary>
/// The HTTP-level fake for the real Google gateways (no GCP access: nothing here ever leaves the process). A route is a
/// method plus an absolute path; each <see cref="Returns(HttpMethod, string, int, string)"/> queues one response for
/// it, in order. A request nobody scripted fails the test loudly with a 599 and is recorded, so an unexpected call can
/// never read as success.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<(string Method, string Path), Queue<Func<HttpResponseMessage>>> _routes = new();

    public List<RecordedRequest> Requests { get; } = [];

    public ScriptedHttpHandler Returns(HttpMethod method, string absolutePath, int status, string json)
    {
        var key = (method.Method, absolutePath);
        if (!_routes.TryGetValue(key, out var queue))
        {
            _routes[key] = queue = new Queue<Func<HttpResponseMessage>>();
        }

        queue.Enqueue(() => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return this;
    }

    public IReadOnlyList<RecordedRequest> To(HttpMethod method, string absolutePath)
        => Requests.Where(r => r.Method == method && r.Uri.AbsolutePath == absolutePath).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        if (_routes.TryGetValue((request.Method.Method, request.RequestUri!.AbsolutePath), out var queue) && queue.Count > 0)
        {
            return queue.Dequeue()();
        }

        return new HttpResponseMessage((HttpStatusCode)599)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":599,\"message\":\"unscripted request {request.Method} {request.RequestUri!.AbsolutePath}\"}}}}", Encoding.UTF8, "application/json"),
        };
    }
}
