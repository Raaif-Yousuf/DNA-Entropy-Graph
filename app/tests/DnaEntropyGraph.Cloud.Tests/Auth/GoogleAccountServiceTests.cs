using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

public class GoogleAccountServiceTests
{
    private static async Task<AccountAuthException> FailureOf(Func<Task> action)
        => (await Should.ThrowAsync<AccountAuthException>(action));

    [Fact]
    public async Task SignIn_uses_loopback_PKCE_S256_and_the_token_request_proves_the_verifier()
    {
        using var harness = new AuthHarness();
        var service = harness.NewService();
        harness.Google.NextIdentity = new FakeIdentity("1001", "first@example.test");

        await service.SignInAsync(CancellationToken.None);

        harness.Google.BrowserLaunches.ShouldBe(1);
        var request = harness.Google.AuthorizationRequests.Single();
        request["client_id"].ShouldBe(AuthHarness.ClientId);
        request["response_type"].ShouldBe("code");
        request["code_challenge_method"].ShouldBe("S256");
        request["code_challenge"].Length.ShouldBeGreaterThanOrEqualTo(43);
        request["prompt"].Split(' ').ShouldContain("select_account");
        request["access_type"].ShouldBe("offline");
        request["state"].ShouldNotBeNullOrWhiteSpace();
        var scopes = request["scope"].Split(' ');
        scopes.ShouldContain("openid");
        scopes.ShouldContain("email");
        scopes.ShouldContain("https://www.googleapis.com/auth/cloud-platform");
        var redirect = new Uri(request["redirect_uri"]);
        redirect.Host.ShouldBe("127.0.0.1");
        redirect.Port.ShouldBeGreaterThan(0);

        var exchange = harness.Google.TokenRequests.Single();
        exchange["grant_type"].ShouldBe("authorization_code");
        exchange["redirect_uri"].ShouldBe(request["redirect_uri"]);
        FakeGoogleOAuth.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"]))).ShouldBe(request["code_challenge"]);
    }

    [Fact]
    public async Task SignIn_stores_the_token_under_the_sub_and_accounts_json_holds_no_token()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");

        service.IsSignedIn.ShouldBeTrue();
        service.CurrentAccount.ShouldBe(new AccountInfo("1001", "first@example.test"));
        var tokenFile = Path.Combine(harness.AuthDirectory, "1001.tok");
        File.Exists(tokenFile).ShouldBeTrue();
        var refresh = harness.Google.RefreshTokenFor("1001");
        Encoding.UTF8.GetString(File.ReadAllBytes(tokenFile)).ShouldNotContain(refresh, Case.Sensitive, "the token file must be encrypted, not JSON on disk");

        var accountsJson = File.ReadAllText(Path.Combine(harness.AuthDirectory, "accounts.json"));
        accountsJson.ShouldNotContain(refresh);
        accountsJson.ShouldNotContain("at-");
        accountsJson.ShouldNotContain("id_token");
        accountsJson.ShouldContain("1001");
    }

    [Fact]
    public async Task A_restart_shows_the_account_and_gets_a_token_with_no_browser()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var launchesBefore = harness.Google.BrowserLaunches;

        var restarted = harness.NewService();

        restarted.IsSignedIn.ShouldBeTrue();
        restarted.CurrentAccount!.Email.ShouldBe("first@example.test");
        (await restarted.GetAccessTokenAsync(CancellationToken.None)).ShouldStartWith("at-");
        harness.Google.BrowserLaunches.ShouldBe(launchesBefore);
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_and_the_refresh_token_is_kept()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30; // inside the library's staleness window, so the next call must refresh
        await harness.SignedInAsync("1001", "first@example.test");
        var original = harness.Google.RefreshTokenFor("1001");

        var service = harness.NewService();
        var token = await service.GetAccessTokenAsync(CancellationToken.None);

        harness.Google.TokenRequests.Count(r => r["grant_type"] == "refresh_token").ShouldBe(1);
        harness.Google.TokenRequests.Last()["refresh_token"].ShouldBe(original);
        token.ShouldStartWith("at-");

        // The refreshed access token was persisted: a restart needs no second refresh. And the stored refresh
        // token survived Google's refresh answer, which omits it: a forced refresh still works with the original.
        var again = harness.NewService();
        await again.GetAccessTokenAsync(CancellationToken.None);
        harness.Google.TokenRequests.Count(r => r["grant_type"] == "refresh_token").ShouldBe(1);
        await ((ICloudTokenRefresher)again).RefreshAsync(CancellationToken.None);
        harness.Google.TokenRequests.Last()["refresh_token"].ShouldBe(original);
        harness.Google.TokenRequests.Count(r => r["grant_type"] == "refresh_token").ShouldBe(2);
    }

    [Fact]
    public async Task Two_accounts_coexist_keyed_by_sub_and_switching_changes_the_token_used()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        var service = await harness.SignedInAsync("1001", "first@example.test");
        await harness.SignedInAsync("2002", "second@example.test", service);

        service.CurrentAccount!.Sub.ShouldBe("2002");
        service.Accounts.Select(a => a.Sub).ShouldBe(["1001", "2002"], ignoreOrder: true);
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue();
        File.Exists(Path.Combine(harness.AuthDirectory, "2002.tok")).ShouldBeTrue();

        var changes = 0;
        service.AccountChanged += (_, _) => changes++;
        await service.SwitchAccountAsync("1001", CancellationToken.None);
        changes.ShouldBe(1);
        service.CurrentAccount!.Email.ShouldBe("first@example.test");
        await service.GetAccessTokenAsync(CancellationToken.None);
        harness.Google.TokenRequests.Last()["refresh_token"].ShouldBe(harness.Google.RefreshTokenFor("1001"));

        // The choice survives a restart.
        harness.NewService().CurrentAccount!.Sub.ShouldBe("1001");

        // The same account signing in again does not duplicate it.
        await harness.SignedInAsync("1001", "first@example.test", service);
        service.Accounts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Switching_to_an_unknown_account_names_the_code_and_changes_nothing()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");

        var failure = await FailureOf(() => service.SwitchAccountAsync("9999", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.AccountNotFound);
        service.CurrentAccount!.Sub.ShouldBe("1001");
    }

    [Fact]
    public async Task A_revoked_refresh_token_is_SIGNIN_EXPIRED_with_a_sign_in_again_action()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.RevokeEverythingAtGoogle();

        var failure = await FailureOf(() => service.GetAccessTokenAsync(CancellationToken.None));

        failure.Code.ShouldBe("SIGNIN_EXPIRED");
        AuthErrorCodes.ActionResourceKey(failure.Code).ShouldBe("AuthAction_SignInAgain");
        service.IsSignedIn.ShouldBeFalse();
        service.CurrentAccount.ShouldBe(new AccountInfo("1001", "first@example.test", NeedsSignIn: true));
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeFalse("a refused token is dead weight and must not stay on disk");

        // The state survives a restart, and signing in again clears it.
        harness.NewService().IsSignedIn.ShouldBeFalse();
        harness.Google.ExpiresInSeconds = 3600;
        await harness.SignedInAsync("1001", "first@example.test", service);
        service.IsSignedIn.ShouldBeTrue();
        service.CurrentAccount!.NeedsSignIn.ShouldBeFalse();
    }

    [Fact]
    public async Task The_token_refresher_forces_a_refresh_even_when_the_access_token_is_still_fresh()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();

        await ((ICloudTokenRefresher)service).RefreshAsync(CancellationToken.None);

        harness.Google.TokenRequests.Count(r => r["grant_type"] == "refresh_token").ShouldBe(1);
    }

    [Fact]
    public async Task No_network_during_a_refresh_is_not_an_expiry()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.NetworkDown = true;

        var failure = await FailureOf(() => service.GetAccessTokenAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.NetworkUnavailable);
        service.IsSignedIn.ShouldBeTrue();
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue();
    }

    [Fact]
    public async Task Sign_out_revokes_at_Google_then_deletes_the_file_and_falls_back_to_the_other_account()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        await harness.SignedInAsync("2002", "second@example.test", service);
        var refresh = harness.Google.RefreshTokenFor("2002");

        var revoked = await service.SignOutAsync(CancellationToken.None);

        revoked.ShouldBeTrue();
        harness.Google.RevokedTokens.ShouldBe([refresh]);
        File.Exists(Path.Combine(harness.AuthDirectory, "2002.tok")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(harness.AuthDirectory, "accounts.json")).ShouldNotContain("2002");
        service.CurrentAccount!.Sub.ShouldBe("1001");

        await service.SignOutAsync(CancellationToken.None);
        service.IsSignedIn.ShouldBeFalse();
        service.CurrentAccount.ShouldBeNull();
        service.Accounts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sign_out_still_deletes_locally_when_Google_cannot_revoke()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        harness.Google.RevokeFails = true;

        var revoked = await service.SignOutAsync(CancellationToken.None);

        revoked.ShouldBeFalse();
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeFalse();
        service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Signing_out_with_nobody_signed_in_is_a_no_op()
    {
        using var harness = new AuthHarness();
        var service = harness.NewService();

        (await service.SignOutAsync(CancellationToken.None)).ShouldBeTrue();
        harness.Google.RevokedTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_client_file_is_a_named_error_before_any_browser_opens()
    {
        using var harness = new AuthHarness(writeClientFile: false);
        var service = harness.NewService();

        var failure = await FailureOf(() => service.SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe("OAUTH_CLIENT_MISSING");
        harness.Google.BrowserLaunches.ShouldBe(0);
        service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task The_user_declining_on_Googles_page_is_SIGNIN_CANCELLED()
    {
        using var harness = new AuthHarness();
        harness.Google.Behaviour = BrowserBehaviour.Deny;
        var service = harness.NewService();

        var failure = await FailureOf(() => service.SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninCancelled);
        service.IsSignedIn.ShouldBeFalse();
        Directory.Exists(harness.AuthDirectory).ShouldBeFalse("a failed sign-in must not leave anything behind");
    }

    [Fact]
    public async Task A_stray_request_with_the_wrong_state_is_refused_and_the_real_redirect_still_completes_the_sign_in()
    {
        using var harness = new AuthHarness();
        harness.Google.Behaviour = BrowserBehaviour.WrongStateThenReal;
        var service = harness.NewService();

        await service.SignInAsync(CancellationToken.None);

        service.IsSignedIn.ShouldBeTrue();
        harness.Google.TokenRequests.Count.ShouldBe(1, "the forged request's code must never be exchanged");
    }

    [Fact]
    public async Task Only_forged_requests_never_sign_anyone_in_and_the_wait_ends_as_a_timeout()
    {
        using var harness = new AuthHarness { SignInTimeout = TimeSpan.FromMilliseconds(500) };
        harness.Google.Behaviour = BrowserBehaviour.WrongStateOnly;
        var service = harness.NewService();

        (await FailureOf(() => service.SignInAsync(CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.SigninTimeout);

        harness.Google.TokenRequests.ShouldBeEmpty();
        service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task A_sign_in_waiting_on_the_browser_does_not_block_the_signed_in_account()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30; // forces a refresh, which takes the service's lock
        var service = await harness.SignedInAsync("1001", "first@example.test");
        harness.Google.Behaviour = BrowserBehaviour.Hang;
        using var cts = new CancellationTokenSource();
        var pending = service.SignInAsync(cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken); // let the sign-in reach its wait on the browser

        var token = await service.GetAccessTokenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.SwitchAccountAsync("1001", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        token.ShouldStartWith("at-");
        pending.IsCompleted.ShouldBeFalse();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_browser_that_cannot_be_opened_is_SIGNIN_BROWSER()
    {
        using var harness = new AuthHarness { BrowserOverride = new ThrowingBrowser(new System.ComponentModel.Win32Exception("no browser registered")) };

        var failure = await FailureOf(() => harness.NewService().SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.BrowserUnavailable);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_is_SIGNIN_STORAGE_not_a_crash()
    {
        using var harness = new AuthHarness();
        File.WriteAllText(harness.AuthDirectory, "a file where the folder should be");

        var failure = await FailureOf(() => harness.NewService().SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.StorageFailed);
    }

    [Fact]
    public async Task A_token_request_that_times_out_is_a_network_problem_not_a_cancellation_or_an_expiry()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.TokenRequestsTimeOut = true;

        var failure = await FailureOf(() => service.GetAccessTokenAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.NetworkUnavailable);
        service.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unrelated_InvalidOperationException_during_a_refresh_surfaces_and_is_not_called_a_network_problem()
    {
        using var harness = new AuthHarness();
        harness.Google.ExpiresInSeconds = 30;
        await harness.SignedInAsync("1001", "first@example.test");
        var service = harness.NewService();
        harness.Google.TokenRequestsThrowABug = true;

        await Should.ThrowAsync<InvalidOperationException>(() => service.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_account_list_that_cannot_be_saved_is_SIGNIN_STORAGE_and_leaves_no_orphan_token_file()
    {
        using var harness = new AuthHarness();
        Directory.CreateDirectory(Path.Combine(harness.AuthDirectory, "accounts.json")); // a folder where the file should be

        var failure = await FailureOf(() => harness.NewService().SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.StorageFailed);
        Directory.GetFiles(harness.AuthDirectory, "*.tok").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_IO_failure_in_the_browser_step_is_not_reported_as_a_failure_to_save()
    {
        using var harness = new AuthHarness { BrowserOverride = new ThrowingBrowser(new IOException("socket broke")) };

        await Should.ThrowAsync<IOException>(() => harness.NewService().SignInAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_production_HTTP_path_with_no_injected_handler_runs_the_flow_up_to_the_users_choice()
    {
        // No handler means Google's own HttpClient stack and the real Google URLs. The user declining needs no
        // network, so this proves the flow is constructible and reaches the browser with the production settings.
        using var harness = new AuthHarness { UseProductionHttp = true };
        harness.Google.Behaviour = BrowserBehaviour.Deny;

        var failure = await FailureOf(() => harness.NewService().SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninCancelled);
        harness.Google.AuthorizationRequests.Single()["code_challenge_method"].ShouldBe("S256");
    }

    private sealed class ThrowingBrowser(Exception failure) : DnaEntropyGraph.Cloud.Auth.IBrowserLauncher
    {
        public Task LaunchAsync(Uri url, CancellationToken cancellationToken) => throw failure;
    }

    [Fact]
    public async Task A_page_never_completed_is_SIGNIN_TIMEOUT_and_cancelling_is_a_cancellation()
    {
        using var harness = new AuthHarness { SignInTimeout = TimeSpan.FromMilliseconds(300) };
        harness.Google.Behaviour = BrowserBehaviour.Hang;
        var service = harness.NewService();

        (await FailureOf(() => service.SignInAsync(CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.SigninTimeout);

        harness.SignInTimeout = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Should.ThrowAsync<OperationCanceledException>(() => harness.NewService().SignInAsync(cts.Token));
    }

    [Theory]
    [InlineData(false, true, "no id token")]
    [InlineData(true, false, "no refresh token")]
    public async Task A_token_answer_without_an_id_or_refresh_token_is_SIGNIN_FAILED_and_stores_nothing(bool idToken, bool refreshToken, string why)
    {
        using var harness = new AuthHarness();
        harness.Google.ReturnIdToken = idToken;
        harness.Google.ReturnRefreshToken = refreshToken;
        var service = harness.NewService();

        var failure = await FailureOf(() => service.SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninFailed, why);
        service.IsSignedIn.ShouldBeFalse();
        Directory.Exists(harness.AuthDirectory).ShouldBeFalse();
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task A_sub_that_is_not_a_safe_file_name_is_refused_and_nothing_is_written_outside_the_auth_folder(string sub)
    {
        using var harness = new AuthHarness();
        var service = harness.NewService();
        harness.Google.NextIdentity = new FakeIdentity(sub, "evil@example.test");

        var failure = await FailureOf(() => service.SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninFailed);
        Directory.GetFiles(harness.Root, "*.tok", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_file_that_cannot_be_decrypted_reads_as_needing_a_new_sign_in()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        File.WriteAllBytes(Path.Combine(harness.AuthDirectory, "1001.tok"), [1, 2, 3, 4]);
        var service = harness.NewService();

        var failure = await FailureOf(() => service.GetAccessTokenAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninExpired);
        service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task A_deleted_token_file_means_signed_out_not_a_crash()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        File.Delete(Path.Combine(harness.AuthDirectory, "1001.tok"));

        var service = harness.NewService();

        service.IsSignedIn.ShouldBeFalse();
        (await FailureOf(() => service.GetAccessTokenAsync(CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.SigninExpired);
    }

    [Fact]
    public async Task Getting_a_token_when_nobody_is_signed_in_says_sign_in()
    {
        using var harness = new AuthHarness();

        (await FailureOf(() => harness.NewService().GetAccessTokenAsync(CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.SigninExpired);
    }

    [Fact]
    public async Task The_accounts_file_is_valid_json_with_the_documented_shape()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.AuthDirectory, "accounts.json")));
        document.RootElement.GetProperty("activeSub").GetString().ShouldBe("1001");
        document.RootElement.GetProperty("accounts").GetArrayLength().ShouldBe(1);
    }
}
