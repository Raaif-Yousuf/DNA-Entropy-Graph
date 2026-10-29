using DnaEntropyGraph.Cloud.Net;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Cloud.Tests.Rest;
using DnaEntropyGraph.Core.Net;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Net;

/// <summary>Issue #239 review round: the two clients that are not Google discovery clients (Cloud Quotas, the NCBI accession lookup) follow the proxy policy too, proved on the wire.</summary>
public class ProxyBareClientTests
{
    private static async Task Swallow(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception)
        {
            // The proxy refuses on purpose; what matters is that the request reached it.
        }
    }

    [Fact]
    public async Task The_quota_client_sends_its_request_to_the_manual_proxy()
    {
        using var proxy = new RefusingProxy(407);
        var policy = ProxyHttpClientFactoryTests.NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);
        using var client = GoogleRestClient.CreateHttpClient(new StubTokenSource(), new GoogleCloudOptions { Proxy = policy });

        await Swallow(() => client.GetAsync("https://cloudquotas.googleapis.com/probe"));

        proxy.Connects.ShouldContain(c => c.StartsWith("CONNECT cloudquotas.googleapis.com:443 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_quota_client_follows_a_policy_change_with_no_new_client()
    {
        using var proxy = new RefusingProxy(407);
        var policy = ProxyHttpClientFactoryTests.NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.None, ""), null);
        using var client = GoogleRestClient.CreateHttpClient(new StubTokenSource(), new GoogleCloudOptions { Proxy = policy });
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);

        await Swallow(() => client.GetAsync("https://cloudquotas.googleapis.com/probe"));

        proxy.Connects.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_proxied_client_for_ncbi_goes_through_the_manual_proxy_and_keeps_its_cap_and_user_agent()
    {
        using var proxy = new RefusingProxy(407);
        var policy = ProxyHttpClientFactoryTests.NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);
        using var client = ProxiedHttpClient.Create(policy, maxResponseContentBufferSize: 1024 * 1024, userAgent: "dna-entropy-graph");

        await Swallow(() => client.GetAsync("https://eutils.ncbi.nlm.nih.gov/probe"));

        proxy.Connects.ShouldContain(c => c.StartsWith("CONNECT eutils.ncbi.nlm.nih.gov:443 ", StringComparison.Ordinal));
        client.MaxResponseContentBufferSize.ShouldBe(1024 * 1024);
        client.DefaultRequestHeaders.UserAgent.ToString().ShouldBe("dna-entropy-graph");
    }
}
