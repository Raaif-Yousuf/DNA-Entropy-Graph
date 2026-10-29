using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>Issue #620 cold review: the edges of merging under the lock (a sign-out in flight, a file damaged under a running copy).</summary>
public class AccountsFileConcurrencyEdgeTests
{
    private static readonly byte[] Damaged = Encoding.UTF8.GetBytes("{\"activeSub\":\"1001\",\"acc");

    private static string AccountsPath(AuthHarness harness) => Path.Combine(harness.AuthDirectory, "accounts.json");

    [Fact]
    public async Task A_project_choice_while_another_copy_is_signing_the_account_out_does_not_bring_it_back()
    {
        using var harness = new AuthHarness();
        var copyA = await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        copyB.IsSignedIn.ShouldBeTrue();
        harness.Google.RevokeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var signOut = copyA.SignOutAsync(CancellationToken.None);
        await harness.Google.RevokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeFalse("the token goes with the list change, under the lock, before the revoke");

        var failure = await Should.ThrowAsync<AccountAuthException>(() => copyB.SelectProjectAsync("deg-proj-one", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninExpired);
        using (var document = JsonDocument.Parse(File.ReadAllText(AccountsPath(harness))))
        {
            document.RootElement.GetProperty("accounts").GetArrayLength().ShouldBe(0, "the account stays signed out");
        }

        harness.Google.RevokeGate.SetResult();
        await signOut;
        harness.NewService().IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_damaged_under_a_running_copy_is_kept_and_the_change_that_found_it_says_so_once()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        File.WriteAllBytes(AccountsPath(harness), Damaged);

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SelectProjectAsync("deg-proj-one", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.AccountsFileUnreadable);
        File.ReadAllBytes(AccountsPath(harness) + ".bad").ShouldBe(Damaged);

        harness.Google.NextIdentity = new FakeIdentity("2002", "second@example.test");
        await service.SignInAsync(CancellationToken.None);
        service.CurrentAccount!.Email.ShouldBe("second@example.test", "told once: the next change goes through");
    }

    [Fact]
    public async Task A_sign_in_that_set_a_damaged_file_aside_does_not_leave_the_notice_pending_for_the_next_change()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        File.WriteAllBytes(AccountsPath(harness), Damaged);
        harness.Google.NextIdentity = new FakeIdentity("2002", "second@example.test");

        await service.SignInAsync(CancellationToken.None);
        await service.SelectProjectAsync("deg-proj-one", CancellationToken.None);

        service.SelectedProjectId.ShouldBe("deg-proj-one", "the user just signed in again: the project choice is not refused with 'sign in again'");
        File.ReadAllBytes(AccountsPath(harness) + ".bad").ShouldBe(Damaged);
    }

    [Fact]
    public async Task A_failed_write_after_a_set_aside_keeps_the_notice_for_the_next_sign_in()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        File.WriteAllBytes(AccountsPath(harness), Damaged);
        service.Registry.BeforeWriteForTest = () => throw new IOException("disk full");

        harness.Google.NextIdentity = new FakeIdentity("2002", "second@example.test");
        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SignInAsync(CancellationToken.None));
        failure.Code.ShouldBe(AuthErrorCodes.StorageFailed);
        service.Registry.BeforeWriteForTest = null;
        service.IsSignedIn.ShouldBeFalse("the list was set aside and is empty");

        var next = await Should.ThrowAsync<AccountAuthException>(() => service.SignInAsync(CancellationToken.None));
        next.Code.ShouldBe(AuthErrorCodes.AccountsFileUnreadable);
        File.ReadAllBytes(AccountsPath(harness) + ".bad").ShouldBe(Damaged);
    }

    [Fact]
    public async Task A_stale_expiry_mark_does_not_override_a_fresh_sign_in_from_another_copy()
    {
        using var harness = new AuthHarness();
        var copyA = await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        File.WriteAllBytes(Path.Combine(harness.AuthDirectory, "1001.tok"), [1, 2, 3]); // A will find its token dead
        copyA.AfterExpiryStampForTest = async () =>
        {
            harness.Google.NextIdentity = new FakeIdentity("1001", "first@example.test");
            await copyB.SignInAsync(CancellationToken.None); // lands after A's stamp, before A's update
        };

        var failure = await Should.ThrowAsync<AccountAuthException>(() => copyA.GetAccessTokenAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninExpired);
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue("the fresh token is not deleted");
        harness.NewService().IsSignedIn.ShouldBeTrue("the fresh sign-in stands: the stale mark did not set NeedsSignIn");
    }

    [Fact]
    public async Task A_sign_in_of_the_same_account_during_the_revoke_of_a_sign_out_keeps_its_new_token()
    {
        using var harness = new AuthHarness();
        var copyA = await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        harness.Google.RevokeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var signOut = copyA.SignOutAsync(CancellationToken.None);
        await harness.Google.RevokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        harness.Google.NextIdentity = new FakeIdentity("1001", "first@example.test");
        await copyB.SignInAsync(CancellationToken.None);
        harness.Google.RevokeGate.SetResult();
        await signOut;

        harness.Google.RevokedTokens.Count.ShouldBe(1);
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue("the sign-out deletes only the token it found, not the one signed in meanwhile");
        harness.NewService().IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task A_damaged_file_the_folder_will_not_let_us_set_aside_is_a_folder_problem_even_when_found_by_a_change()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        File.WriteAllBytes(AccountsPath(harness), Damaged);

        using (new FileStream(AccountsPath(harness), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SelectProjectAsync("deg-proj-one", CancellationToken.None));

            failure.Code.ShouldBe(AuthErrorCodes.StorageFailed, "Try again would never fix a folder that will not let the file move");
            service.IsSignedIn.ShouldBeFalse("the cached list was dropped: the file is damaged, not the list we hold");
        }
    }

    private static async Task<string> RefreshTokenOnDiskAsync(AuthHarness harness, string sub)
        => (await new DpapiTokenStore(harness.AuthDirectory, new XorProtector()).GetAsync<Google.Apis.Auth.OAuth2.Responses.TokenResponse>(sub))!.RefreshToken;

    [Fact]
    public async Task A_sign_in_of_the_same_account_between_a_sign_outs_stamp_and_its_list_change_is_not_signed_out()
    {
        using var harness = new AuthHarness();
        var copyA = await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        copyA.AfterSignOutStampForTest = async () =>
        {
            harness.Google.NextIdentity = new FakeIdentity("1001", "first@example.test");
            await copyB.SignInAsync(CancellationToken.None);
        };

        await copyA.SignOutAsync(CancellationToken.None);

        harness.Google.RevokedTokens.ShouldBeEmpty("the token the sign-out found is gone: nothing of the fresh sign-in is revoked");
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue();
        harness.NewService().IsSignedIn.ShouldBeTrue("the account the other copy just signed in stays listed");
    }

    [Fact]
    public async Task A_sign_out_revokes_the_token_it_found_not_one_signed_in_after_the_list_change()
    {
        using var harness = new AuthHarness();
        var copyA = await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        var old = await RefreshTokenOnDiskAsync(harness, "1001");
        copyA.AfterSignOutListUpdateForTest = async () =>
        {
            harness.Google.NextIdentity = new FakeIdentity("1001", "first@example.test");
            await copyB.SignInAsync(CancellationToken.None);
        };

        await copyA.SignOutAsync(CancellationToken.None);

        harness.Google.RevokedTokens.ShouldBe([old]);
        (await RefreshTokenOnDiskAsync(harness, "1001")).ShouldNotBe(old);
        harness.NewService().IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task A_sign_out_whose_token_file_cannot_be_deleted_still_lists_the_account_out_and_revokes_then_says_storage()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        var tokenPath = Path.Combine(harness.AuthDirectory, "1001.tok");
        FileStream? hold = null;
        service.AfterSignOutStampForTest = () =>
        {
            hold = new FileStream(tokenPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        };

        using (hold)
        {
            var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SignOutAsync(CancellationToken.None));

            failure.Code.ShouldBe(AuthErrorCodes.StorageFailed);
        }

        harness.Google.RevokedTokens.Count.ShouldBe(1, "the list change was written, so the revoke still ran");
        harness.NewService().IsSignedIn.ShouldBeFalse("the list no longer has the account");
    }
}