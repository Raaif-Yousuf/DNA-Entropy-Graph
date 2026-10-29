using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Net;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #239: the update check (Velopack against the GitHub Releases feed) follows the proxy policy like every other client. Proved on the wire.</summary>
public class ProxiedFileDownloaderTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value) => _values[key] = value;
    }

    private sealed class NoCredentials : IProxyCredentialStore
    {
        public System.Net.NetworkCredential? Load() => null;

        public void Save(System.Net.NetworkCredential credential)
        {
        }

        public void Clear()
        {
        }
    }

    [Fact]
    public async Task The_update_check_sends_its_github_request_to_the_manual_proxy()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var heads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            // Velopack may try the request more than once; every connection gets the same refusal.
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var buffer = new byte[2048];
                        var read = await client.GetStream().ReadAsync(buffer, TestContext.Current.CancellationToken);
                        heads.Enqueue(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                        await client.GetStream().WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"x\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
                    }
                }, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);
        var policy = new ProxyPolicy(new MemorySettings(), new NoCredentials());
        policy.Apply(new ProxySettings(ProxyMode.Manual, $"127.0.0.1:{port}"), null);
        var downloader = new ProxiedFileDownloader(policy);

        var failure = await Should.ThrowAsync<Exception>(() => downloader.DownloadString("https://api.github.com/repos/Raaif-Yousuf/DNA-Entropy-Graph/releases", null, 0.2));

        heads.ShouldNotBeEmpty();
        heads.First().ShouldStartWith("CONNECT api.github.com:443 ");
        ProxyFailure.Detect(failure).ShouldBe(ProxyFailureKind.ProxyRefused);
    }
}
