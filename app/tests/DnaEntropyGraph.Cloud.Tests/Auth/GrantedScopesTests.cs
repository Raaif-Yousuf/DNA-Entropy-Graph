using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>Issue #259: under Google's granular consent the user can untick cloud-platform; the app must notice at sign-in and on refresh.</summary>
public class GrantedScopesTests
{
    private const string WithoutCloud = "openid https://www.googleapis.com/auth/userinfo.email";

    [Fact]
    public async Task A_sign_in_without_cloud_platform_is_CONSENT_INCOMPLETE_and_stores_nothing()
    {
        using var harness = new AuthHarness();
        harness.Google.SignInGrantedScope = WithoutCloud;
        var service = harness.NewService();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.ConsentIncomplete);
        AuthErrorCodes.ActionResourceKey(failure.Code).ShouldBe("AuthAction_AllowAccess");
        service.IsSignedIn.ShouldBeFalse();
        service.Accounts.ShouldBeEmpty();
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeFalse("a grant missing the needed scope must not be kept");
    }

    [Fact]
    public async Task Signing_in_again_with_the_scope_granted_succeeds_and_asks_for_consent_again()
    {
        using var harness = new AuthHarness();
        harness.Google.SignInGrantedScope = WithoutCloud;
        var service = harness.NewService();
        await Should.ThrowAsync<AccountAuthException>(() => service.SignInAsync(CancellationToken.None));

        harness.Google.SignInGrantedScope = null;
        await service.SignInAsync(CancellationToken.None);

        service.IsSignedIn.ShouldBeTrue();
        harness.Google.AuthorizationRequests.Count.ShouldBe(2);
        harness.Google.AuthorizationRequests.Last()["prompt"].Split(' ').ShouldContain("consent");
    }

    [Fact]
    public async Task A_refresh_answer_that_lacks_cloud_platform_marks_the_account_sign_in_again_with_CONSENT_INCOMPLETE()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.RefreshGrantedScope = WithoutCloud;

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.GetAccessTokenAsync(null, CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.ConsentIncomplete);
        service.IsSignedIn.ShouldBeFalse();
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refresh_answer_that_keeps_cloud_platform_is_fine()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.RefreshGrantedScope = "openid https://www.googleapis.com/auth/cloud-platform";

        (await service.GetAccessTokenAsync(null, CancellationToken.None)).ShouldStartWith("at-");
    }

    [Fact]
    public async Task A_refresh_answer_that_names_no_scope_keeps_the_stored_grant_and_the_sign_in()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.RefreshGrantedScope.ShouldBeNull("the fake answers the refresh with no scope field");

        (await service.GetAccessTokenAsync(null, CancellationToken.None)).ShouldStartWith("at-");

        harness.Google.TokenRequests.Count(r => r["grant_type"] == "refresh_token").ShouldBe(1, "the refresh really happened");
        service.IsSignedIn.ShouldBeTrue();
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue();
    }
}
