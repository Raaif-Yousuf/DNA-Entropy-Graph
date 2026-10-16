using System.Net;
using System.Security.Authentication;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Net;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #239: the one proxy policy (System, Manual, None), where it is saved, how it answers per request, and what a proxy failure looks like.</summary>
public class ProxyPolicyTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public bool FailWrites { get; set; }

        public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value)
        {
            if (FailWrites)
            {
                throw new SettingsUnavailableException("locked");
            }

            Values[key] = value;
        }
    }

    private sealed class MemoryCredentials : IProxyCredentialStore
    {
        public NetworkCredential? Saved { get; set; }

        public NetworkCredential? Load() => Saved;

        public void Save(NetworkCredential credential) => Saved = credential;

        public void Clear() => Saved = null;
    }

    private sealed class FixedProxy(Uri? answer) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => answer;

        public bool IsBypassed(Uri host) => answer is null;
    }

    private static readonly Uri Google = new("https://storage.googleapis.com/");

    private static NetworkCredential? Offered(PolicyWebProxy proxy) => proxy.Credentials!.GetCredential(new Uri("http://proxy.uni.edu:8080"), "Basic");

    [Theory]
    [InlineData("proxy.uni.edu:8080", "http://proxy.uni.edu:8080/")]
    [InlineData("  http://proxy.uni.edu:3128  ", "http://proxy.uni.edu:3128/")]
    [InlineData("10.0.0.5:80", "http://10.0.0.5/")]
    public void A_manual_address_is_host_and_port_with_an_optional_http_scheme(string text, string expected)
    {
        ProxyAddress.TryParse(text, out var uri).ShouldBeTrue();
        uri.AbsoluteUri.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("proxy.uni.edu")]
    [InlineData("proxy.uni.edu:0")]
    [InlineData("proxy.uni.edu:99999")]
    [InlineData("https://proxy.uni.edu:8080")]
    [InlineData("socks5://proxy.uni.edu:1080")]
    [InlineData("user:secret@proxy.uni.edu:8080")]
    [InlineData("http://user:secret@proxy.uni.edu:8080")]
    [InlineData("two words:8080")]
    [InlineData("proxy.uni.edu:8080/path")]
    public void An_address_that_is_not_a_plain_host_and_port_is_refused_and_a_password_never_goes_in_the_address(string? text)
        => ProxyAddress.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Nothing_saved_means_the_system_setting_and_a_saved_choice_round_trips()
    {
        var settings = new MemorySettings();
        var store = new ProxySettingsStore(settings);

        store.Load().ShouldBe(new ProxySettings(ProxyMode.System, string.Empty));

        store.Save(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"));
        store.Load().ShouldBe(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"));
        settings.Values[ProxySettingKeys.Mode].ShouldBe("manual");

        store.Save(new ProxySettings(ProxyMode.None, string.Empty));
        store.Load().Mode.ShouldBe(ProxyMode.None);
    }

    [Fact]
    public void An_unreadable_mode_is_the_system_setting_not_a_crash_and_the_password_is_never_in_settings_json()
    {
        var settings = new MemorySettings();
        settings.Values[ProxySettingKeys.Mode] = "turbo";

        new ProxySettingsStore(settings).Load().Mode.ShouldBe(ProxyMode.System);

        var policy = new ProxyPolicy(settings, new MemoryCredentials());
        policy.Apply(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"), new NetworkCredential("alice", "hunter2"));
        settings.Values.Values.ShouldNotContain(v => v.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void System_mode_asks_the_operating_system_and_answers_its_credentials_to_a_proxy_that_wants_a_sign_in()
    {
        var os = new FixedProxy(new Uri("http://pac-chose.uni.edu:8080"));
        var proxy = new PolicyWebProxy(new ProxyPolicy(new MemorySettings(), new MemoryCredentials()), os);

        proxy.GetProxy(Google).ShouldBe(new Uri("http://pac-chose.uni.edu:8080"));
        proxy.IsBypassed(Google).ShouldBeFalse();
        Offered(proxy).ShouldBeSameAs(CredentialCache.DefaultNetworkCredentials);
    }

    [Fact]
    public void System_mode_with_no_proxy_in_the_os_goes_direct()
    {
        var proxy = new PolicyWebProxy(new ProxyPolicy(new MemorySettings(), new MemoryCredentials()), new FixedProxy(null));

        proxy.GetProxy(Google).ShouldBeNull();
        proxy.IsBypassed(Google).ShouldBeTrue();
    }

    [Fact]
    public void Manual_mode_uses_the_typed_proxy_for_the_internet_and_not_for_this_computer_and_carries_the_saved_sign_in()
    {
        var policy = new ProxyPolicy(new MemorySettings(), new MemoryCredentials());
        policy.Apply(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"), new NetworkCredential("alice", "hunter2"));
        var proxy = new PolicyWebProxy(policy, new FixedProxy(new Uri("http://not-this.example:1")));

        proxy.GetProxy(Google).ShouldBe(new Uri("http://proxy.uni.edu:8080"));
        proxy.GetProxy(new Uri("http://localhost:12345/")).ShouldBeNull();
        proxy.GetProxy(new Uri("http://127.0.0.1:12345/")).ShouldBeNull();
        var credential = Offered(proxy).ShouldBeOfType<NetworkCredential>();
        credential.UserName.ShouldBe("alice");
        credential.Password.ShouldBe("hunter2");
    }

    [Fact]
    public void Manual_mode_with_no_saved_sign_in_still_tries_the_windows_sign_in()
    {
        var policy = new ProxyPolicy(new MemorySettings(), new MemoryCredentials());
        policy.Apply(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"), null);

        Offered(new PolicyWebProxy(policy, new FixedProxy(null))).ShouldBeSameAs(CredentialCache.DefaultNetworkCredentials);
    }

    [Fact]
    public void Manual_mode_with_an_address_that_cannot_be_read_falls_back_to_the_system_setting_rather_than_going_dark()
    {
        var policy = new ProxyPolicy(new MemorySettings(), new MemoryCredentials());
        policy.Apply(new ProxySettings(ProxyMode.Manual, "not a proxy"), null);
        var proxy = new PolicyWebProxy(policy, new FixedProxy(new Uri("http://sys.uni.edu:8080")));

        proxy.GetProxy(Google).ShouldBe(new Uri("http://sys.uni.edu:8080"));
    }

    [Fact]
    public void None_mode_goes_direct_even_when_the_os_has_a_proxy()
    {
        var policy = new ProxyPolicy(new MemorySettings(), new MemoryCredentials());
        policy.Apply(new ProxySettings(ProxyMode.None, string.Empty), null);
        var proxy = new PolicyWebProxy(policy, new FixedProxy(new Uri("http://sys.uni.edu:8080")));

        proxy.GetProxy(Google).ShouldBeNull();
        proxy.IsBypassed(Google).ShouldBeTrue();
    }

    [Fact]
    public void A_change_applies_to_the_very_next_request_on_the_same_proxy_object_and_is_saved_for_the_next_launch()
    {
        var settings = new MemorySettings();
        var credentials = new MemoryCredentials();
        var policy = new ProxyPolicy(settings, credentials);
        var proxy = new PolicyWebProxy(policy, new FixedProxy(null));

        proxy.GetProxy(Google).ShouldBeNull();
        policy.Apply(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"), new NetworkCredential("alice", "hunter2"));
        proxy.GetProxy(Google).ShouldBe(new Uri("http://proxy.uni.edu:8080"));

        var relaunched = new ProxyPolicy(settings, credentials);
        relaunched.Settings.ShouldBe(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"));
        relaunched.ManualCredential!.UserName.ShouldBe("alice");

        policy.Apply(new ProxySettings(ProxyMode.System, string.Empty), null);
        credentials.Saved.ShouldBeNull();
    }

    [Fact]
    public void An_unreadable_credential_file_is_no_credential_not_a_crash()
    {
        var policy = new ProxyPolicy(new MemorySettings(), new ThrowingCredentials());

        policy.ManualCredential.ShouldBeNull();
    }

    private sealed class ThrowingCredentials : IProxyCredentialStore
    {
        public NetworkCredential? Load() => throw new ProxyCredentialUnreadableException("bad file", null);

        public void Save(NetworkCredential credential) => throw new ProxyCredentialUnreadableException("bad file", null);

        public void Clear() => throw new ProxyCredentialUnreadableException("bad file", null);
    }

    [Fact]
    public void A_proxy_that_wants_a_sign_in_is_named_by_the_tunnel_status_not_by_the_message()
    {
        var failure = new HttpRequestException(HttpRequestError.ProxyTunnelError, "x", null, HttpStatusCode.ProxyAuthenticationRequired);

        ProxyFailure.Detect(failure).ShouldBe(ProxyFailureKind.ProxyRefused);
    }

    [Fact]
    public void A_proxy_that_refuses_the_tunnel_is_a_proxy_failure_wherever_in_the_chain_it_sits()
    {
        var inner = new HttpRequestException(HttpRequestError.ProxyTunnelError, "x", null, HttpStatusCode.Forbidden);

        ProxyFailure.Detect(new InvalidOperationException("outer", inner)).ShouldBe(ProxyFailureKind.ProxyRefused);
    }

    [Fact]
    public void The_credentials_object_a_handler_keeps_follows_the_policy_after_it_was_taken()
    {
        var policy = new ProxyPolicy(new MemorySettings(), new MemoryCredentials());
        policy.Apply(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"), new NetworkCredential("alice", "first"));
        var proxy = new PolicyWebProxy(policy, new FixedProxy(null));
        var kept = proxy.Credentials!;

        policy.Apply(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"), new NetworkCredential("alice", "second"));

        kept.GetCredential(new Uri("http://proxy.uni.edu:8080"), "Basic")!.Password.ShouldBe("second");
        policy.Apply(new ProxySettings(ProxyMode.None, string.Empty), null);
        kept.GetCredential(new Uri("http://proxy.uni.edu:8080"), "Basic").ShouldBeNull();
    }

    [Fact]
    public void A_proxy_failure_is_never_a_proxy_refusal_just_because_a_message_says_so()
        => ProxyFailure.Detect(new HttpRequestException("407 proxy authentication required")).ShouldBeNull();
    [Fact]
    public void Only_a_marked_untrusted_chain_is_the_interception_case()
    {
        var failure = new HttpRequestException(HttpRequestError.SecureConnectionError, "ssl", new AuthenticationException("handshake"));
        failure.Data[ProxyFailure.UntrustedChainKey] = true;

        ProxyFailure.Detect(failure).ShouldBe(ProxyFailureKind.CertificateUntrusted);
        ProxyFailure.Detect(new InvalidOperationException("outer", failure)).ShouldBe(ProxyFailureKind.CertificateUntrusted);
    }

    [Fact]
    public void A_handshake_that_failed_without_proof_of_an_untrusted_chain_stays_a_plain_network_failure()
    {
        // An EOF or reset during the handshake, a captive portal and a wrong clock raise these same types.
        ProxyFailure.Detect(new HttpRequestException(HttpRequestError.SecureConnectionError, "eof", new AuthenticationException("The decryption operation failed"))).ShouldBeNull();
        ProxyFailure.Detect(new AuthenticationException("anything")).ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, ProxyFailureKind.ProxyRefused)]
    [InlineData(HttpStatusCode.Forbidden, ProxyFailureKind.ProxyRefused)]
    public void A_proxy_that_refuses_the_tunnel_with_a_sign_in_or_a_ban_is_a_proxy_failure(HttpStatusCode status, ProxyFailureKind expected)
        => ProxyFailure.Detect(new HttpRequestException(HttpRequestError.ProxyTunnelError, "x", null, status)).ShouldBe(expected);

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(null)]
    public void A_proxy_that_is_merely_unwell_is_a_transient_network_failure(HttpStatusCode? status)
        => ProxyFailure.Detect(new HttpRequestException(HttpRequestError.ProxyTunnelError, "x", null, status)).ShouldBeNull();

    [Fact]
    public void A_credential_failure_after_nothing_changed_leaves_settings_and_policy_alone()
    {
        var settings = new MemorySettings();
        var policy = new ProxyPolicy(settings, new ThrowingCredentials());

        Should.Throw<ProxyCredentialUnreadableException>(() => policy.Apply(new ProxySettings(ProxyMode.Manual, "proxy.uni.edu:8080"), new NetworkCredential("a", "b")));

        settings.Values.ShouldBeEmpty();
        policy.Settings.Mode.ShouldBe(ProxyMode.System);
    }

    [Fact]
    public void A_settings_failure_after_the_sign_in_was_saved_puts_the_old_sign_in_back()
    {
        var settings = new MemorySettings();
        var credentials = new MemoryCredentials { Saved = new NetworkCredential("old", "oldpw") };
        settings.Values[ProxySettingKeys.Mode] = "manual";
        settings.Values[ProxySettingKeys.Address] = "old.uni.edu:8080";
        var policy = new ProxyPolicy(settings, credentials);
        policy.Settings.ManualAddress.ShouldBe("old.uni.edu:8080");
        settings.FailWrites = true;

        Should.Throw<SettingsUnavailableException>(() => policy.Apply(new ProxySettings(ProxyMode.Manual, "new.uni.edu:8080"), new NetworkCredential("new", "newpw")));

        credentials.Saved!.UserName.ShouldBe("old");
        policy.ManualCredential!.UserName.ShouldBe("old");
        policy.Settings.ManualAddress.ShouldBe("old.uni.edu:8080");
        settings.Values[ProxySettingKeys.Address].ShouldBe("old.uni.edu:8080");
    }

    [Fact]
    public void A_settings_failure_when_the_new_choice_has_no_sign_in_restores_the_old_one_too()
    {
        var settings = new MemorySettings();
        var credentials = new MemoryCredentials { Saved = new NetworkCredential("old", "oldpw") };
        settings.Values[ProxySettingKeys.Mode] = "manual";
        settings.Values[ProxySettingKeys.Address] = "old.uni.edu:8080";
        var policy = new ProxyPolicy(settings, credentials);
        policy.ManualCredential.ShouldNotBeNull();
        settings.FailWrites = true;

        Should.Throw<SettingsUnavailableException>(() => policy.Apply(new ProxySettings(ProxyMode.None, string.Empty), null));

        credentials.Saved!.UserName.ShouldBe("old");
    }
    [Fact]
    public void Ordinary_failures_are_not_proxy_failures()
    {
        ProxyFailure.Detect(new HttpRequestException(HttpRequestError.ConnectionError, "refused")).ShouldBeNull();
        ProxyFailure.Detect(new HttpRequestException(HttpRequestError.NameResolutionError, "dns")).ShouldBeNull();
        ProxyFailure.Detect(new TimeoutException()).ShouldBeNull();
        ProxyFailure.Detect(new HttpRequestException("plain", null, HttpStatusCode.NotFound)).ShouldBeNull();
        ProxyFailure.Detect(null).ShouldBeNull();
    }

    [Fact]
    public void A_proxy_authentication_status_from_an_api_response_is_a_proxy_refusal()
    {
        ProxyFailure.IsProxyAuthStatus(407).ShouldBeTrue();
        ProxyFailure.IsProxyAuthStatus(401).ShouldBeFalse();
        ProxyFailure.IsProxyAuthStatus(null).ShouldBeFalse();
    }
}
