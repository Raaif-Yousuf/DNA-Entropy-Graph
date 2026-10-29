using DnaEntropyGraph.Cloud.Tests.Auth;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Net;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Net;

/// <summary>Issue #239: the sign-in's token exchange goes through the proxy policy, and a proxy that refuses it says so by its own code, not "Google could not be reached".</summary>
public class ProxySignInTests
{
    [Fact]
    public async Task A_token_refresh_behind_a_proxy_that_wants_a_sign_in_fails_as_the_proxy_code_and_keeps_the_stored_sign_in()
    {
        using var proxy = new RefusingProxy(407);
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var policy = ProxyHttpClientFactoryTests.NewPolicy();
        policy.Apply(new ProxySettings(ProxyMode.Manual, proxy.Address), null);
        harness.UseProductionHttp = true;
        harness.Proxy = policy;
        var service = harness.NewService();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.GetAccessTokenAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninProxy);
        proxy.Connects.ShouldContain(c => c.Contains("googleapis.com:443", StringComparison.Ordinal));
        service.IsSignedIn.ShouldBeTrue();
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_proxy_a_dead_network_is_still_the_plain_network_code()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.NetworkDown = true;

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.GetAccessTokenAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.NetworkUnavailable);
    }
}
