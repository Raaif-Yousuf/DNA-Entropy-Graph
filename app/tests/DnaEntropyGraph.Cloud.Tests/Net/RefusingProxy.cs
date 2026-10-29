using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DnaEntropyGraph.Cloud.Tests.Net;

/// <summary>
/// A real HTTP proxy on loopback that only ever answers a CONNECT with a fixed refusal, and records what it was asked. It is how the
/// tests see, on the wire, whether a client really went through the proxy and what it sent (issue #239): a mock of the handler would
/// pass even if no client used the policy.
/// </summary>
internal sealed class RefusingProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public RefusingProxy(int status = 407)
    {
        Status = status;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Status { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Address => $"127.0.0.1:{Port}";

    /// <summary>The raw request head of every connection, in order.</summary>
    public List<string> Requests { get; } = [];

    public IEnumerable<string> Connects => Requests.Where(r => r.StartsWith("CONNECT ", StringComparison.Ordinal));

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false) == 1)
                {
                    head.Append((char)buffer[0]);
                }

                lock (Requests)
                {
                    Requests.Add(head.ToString());
                }

                var reason = Status switch { 407 => "Proxy Authentication Required", 403 => "Forbidden", 503 => "Service Unavailable", 502 => "Bad Gateway", _ => "Error" };
                var challenge = Status == 407 ? "Proxy-Authenticate: Basic realm=\"lab\"\r\n" : string.Empty;
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} {reason}\r\n{challenge}Content-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
