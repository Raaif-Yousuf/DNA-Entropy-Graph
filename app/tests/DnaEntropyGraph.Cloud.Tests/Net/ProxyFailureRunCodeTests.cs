using System.Net;
using System.Security.Authentication;
using DnaEntropyGraph.Cloud.Tests.Rest;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Net;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Net;

/// <summary>Issue #239: a proxy failure is permanent for the call (no retries, no offline banner) and is recorded under a code that names the one action.</summary>
public class ProxyFailureRunCodeTests
{
    private static CloudCallPipeline NewPipeline() => new(
        new CloudRetryOptions { MaxRetryAttempts = 3, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
        new StubTokenSource(),
        new CloudRetryLog());

    [Fact]
    public async Task A_proxy_refusal_is_tried_once_and_recorded_as_the_proxy_code_with_the_network_kind()
    {
        var attempts = 0;
        var pipeline = NewPipeline();

        var failure = await Should.ThrowAsync<CloudOperationException>(() => pipeline.ExecuteAsync<int>(
            "test.call",
            _ =>
            {
                attempts++;
                throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "tunnel", null, HttpStatusCode.ProxyAuthenticationRequired);
            },
            CancellationToken.None));

        attempts.ShouldBe(1);
        failure.Kind.ShouldBe(CloudErrorKind.Network);
        failure.Error.Code.ShouldBe(ProxyFailure.RefusedCode);
        RunErrorCodes.For(failure.Error, failure.Kind).ShouldBe(RunErrorCodes.ProxyBlocked);
        pipeline.IsOffline.ShouldBeFalse();
    }

    [Fact]
    public async Task An_untrusted_certificate_is_tried_once_and_recorded_as_the_certificate_code()
    {
        var attempts = 0;

        var failure = await Should.ThrowAsync<CloudOperationException>(() => NewPipeline().ExecuteAsync<int>(
            "test.call",
            _ =>
            {
                attempts++;
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "tls", new AuthenticationException("UntrustedRoot"));
            },
            CancellationToken.None));

        attempts.ShouldBe(1);
        RunErrorCodes.For(failure.Error, failure.Kind).ShouldBe(RunErrorCodes.NetworkCertificate);
    }

    [Fact]
    public async Task An_ordinary_connection_failure_is_still_retried_and_stays_the_plain_network_code()
    {
        var attempts = 0;

        var failure = await Should.ThrowAsync<CloudOperationException>(() => NewPipeline().ExecuteAsync<int>(
            "test.call",
            _ =>
            {
                attempts++;
                throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            },
            CancellationToken.None));

        attempts.ShouldBe(4);
        RunErrorCodes.For(failure.Error, failure.Kind).ShouldBe("network");
    }

    [Fact]
    public void A_407_status_on_an_api_answer_is_a_proxy_refusal_and_not_a_google_error()
    {
        var error = new CloudError(null, 407, "Proxy Authentication Required");

        CloudErrorClassifier.Classify(error).ShouldBe(CloudErrorKind.Network);
        RunErrorCodes.For(error, CloudErrorKind.Network).ShouldBe(RunErrorCodes.ProxyBlocked);
        RunErrorCodes.For(new CloudError(null, 503, "unavailable"), CloudErrorKind.Network).ShouldBe("network");
    }

    [Fact]
    public void Each_new_code_asks_for_one_action_and_is_in_the_roster()
    {
        RunErrorCodes.All.ShouldContain(RunErrorCodes.ProxyBlocked);
        RunErrorCodes.All.ShouldContain(RunErrorCodes.NetworkCertificate);
        RunErrorActions.TryGet(RunErrorCodes.ProxyBlocked, out var proxyAction).ShouldBeTrue();
        proxyAction.ShouldBe(RunErrorAction.OpenSettings);
        RunErrorActions.TryGet(RunErrorCodes.NetworkCertificate, out var certAction).ShouldBeTrue();
        certAction.ShouldBe(RunErrorAction.AskAdministrator);
        AuthErrorCodes.All.ShouldContain(AuthErrorCodes.SigninProxy);
        AuthErrorCodes.All.ShouldContain(AuthErrorCodes.SigninCertificate);
    }
}
