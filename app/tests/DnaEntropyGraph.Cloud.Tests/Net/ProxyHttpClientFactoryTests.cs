using System.Net;
using DnaEntropyGraph.Cloud.Net;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Cloud.Tests.Rest;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Net;
using Google.Apis.Http;
using Google.Apis.Storage.v1;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Net;

/// <summary>Issue #239: every real client really goes through the one proxy policy, with the sign-in the policy holds. Proved on the wire against a loopback proxy.</summary>
public class ProxyHttpClientFactoryTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value) => _values[key] = value;
    }

    private sealed class MemoryCredentials : IProxyCredentialStore
    {
        public NetworkCredential? Load() => null;

        public void Save(NetworkCredential credential)
        {
        }

        public void Clear()
        {
        }
    }

    internal static ProxyPolicy NewPolicy() => new(new MemorySettings(), new MemoryCredentials());

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

    [Fact]
    public async Task A_client_from_the_factory_sends_its_https_request_to_the_manual_proxy_and_a_407_is_a_proxy_refusal()
    {
        using var proxy = new RefusingProxy(407);
        var policy = NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);
        using var client = new ProxyHttpClientFactory(policy).CreateHttpClient(new CreateHttpClientArgs());

        var failure = await FailureOf(() => client.GetAsync("https://storage.googleapis.com/probe"));

        proxy.Connects.ShouldContain(c => c.StartsWith("CONNECT storage.googleapis.com:443 ", StringComparison.Ordinal));
        ProxyFailure.Detect(failure).ShouldBe(ProxyFailureKind.ProxyRefused);
    }

    [Fact]
    public async Task The_saved_manual_sign_in_is_what_the_proxy_is_answered_with()
    {
        using var proxy = new RefusingProxy(407);
        var policy = NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), new NetworkCredential("alice", "hunter2"));
        using var client = new ProxyHttpClientFactory(policy).CreateHttpClient(new CreateHttpClientArgs());

        await FailureOf(() => client.GetAsync("https://storage.googleapis.com/probe"));

        var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String("alice:hunter2"u8.ToArray());
        proxy.Requests.ShouldContain(r => r.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_the_policy_moves_the_very_next_request_off_the_proxy_with_no_new_client()
    {
        using var proxy = new RefusingProxy(407);
        var policy = NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);
        using var client = new ProxyHttpClientFactory(policy).CreateHttpClient(new CreateHttpClientArgs());
        await FailureOf(() => client.GetAsync("https://deg-proxy-probe.invalid/one"));
        var seen = proxy.Requests.Count;
        seen.ShouldBeGreaterThan(0);

        policy.Apply(new ProxySettings(ProxyMode.None, string.Empty), null);
        var direct = await FailureOf(() => client.GetAsync("https://deg-proxy-probe.invalid/two"));

        proxy.Requests.Count.ShouldBe(seen);
        ProxyFailure.Detect(direct).ShouldBeNull();
    }

    [Fact]
    public async Task A_real_google_discovery_client_built_by_the_gateway_factory_goes_through_the_proxy()
    {
        using var proxy = new RefusingProxy(407);
        var policy = NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);
        var initializer = GoogleRestClient.CreateInitializer(new StubTokenSource(), new GoogleCloudOptions { Proxy = policy });
        using var storage = new StorageService(initializer);

        var failure = await FailureOf(() => storage.Buckets.Get("some-bucket").ExecuteAsync());

        proxy.Connects.ShouldContain(c => c.Contains("storage.googleapis.com:443", StringComparison.Ordinal));
        ProxyFailure.Detect(failure).ShouldBe(ProxyFailureKind.ProxyRefused);
    }

    [Fact]
    public void A_build_with_no_policy_still_uses_the_system_proxy_with_the_windows_sign_in()
    {
        var initializer = GoogleRestClient.CreateInitializer(new StubTokenSource(), new GoogleCloudOptions());

        initializer.HttpClientFactory.ShouldBeOfType<ProxyHttpClientFactory>();
    }

    [Fact]
    public void A_scripted_test_handler_still_replaces_the_network_entirely()
    {
        var initializer = GoogleRestClient.CreateInitializer(new StubTokenSource(), new GoogleCloudOptions { HttpHandler = new ScriptedHttpHandler(), Proxy = NewPolicy() });

        initializer.HttpClientFactory.ShouldNotBeOfType<ProxyHttpClientFactory>();
    }
}
