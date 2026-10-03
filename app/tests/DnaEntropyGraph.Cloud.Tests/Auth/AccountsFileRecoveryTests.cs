using System.Text;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>Issue #616 through the real service path: a damaged <c>accounts.json</c> is kept, and the user is told once what to do.</summary>
public class AccountsFileRecoveryTests
{
    private static readonly byte[] Damaged = Encoding.UTF8.GetBytes("{\"activeSub\":\"1001\",\"acc");

    [Fact]
    public async Task A_damaged_accounts_file_is_kept_and_the_sign_in_that_would_have_overwritten_it_says_what_to_do()
    {
        using var harness = new AuthHarness();
        Directory.CreateDirectory(harness.AuthDirectory);
        var path = Path.Combine(harness.AuthDirectory, "accounts.json");
        File.WriteAllBytes(path, Damaged);
        var service = harness.NewService();
        harness.Google.NextIdentity = new FakeIdentity("3003", "third@example.test");

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SignInAsync(CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.AccountsFileUnreadable);
        File.ReadAllBytes(path + ".bad").ShouldBe(Damaged);
        File.Exists(path).ShouldBeFalse("nothing was written over it");
        AuthErrorCodes.ActionResourceKey(failure.Code).ShouldBe("AuthAction_SignInAgain");

        await service.SignInAsync(CancellationToken.None);

        service.CurrentAccount!.Email.ShouldBe("third@example.test");
        File.ReadAllBytes(path + ".bad").ShouldBe(Damaged);
    }

    [Fact]
    public async Task A_missing_accounts_file_signs_in_without_any_notice()
    {
        using var harness = new AuthHarness();

        await harness.SignedInAsync("1001", "first@example.test");

        Directory.GetFiles(harness.AuthDirectory, "accounts.json.bad*").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_locked_accounts_file_is_not_overwritten_and_the_next_call_after_the_lock_clears_reads_the_real_accounts()
    {
        using var harness = new AuthHarness();
        var first = await harness.SignedInAsync("1001", "first@example.test");
        var path = Path.Combine(harness.AuthDirectory, "accounts.json");
        var original = File.ReadAllBytes(path);
        var clock = new ManualClock();
        harness.Clock = clock;
        var service = harness.NewService();
        harness.Google.NextIdentity = new FakeIdentity("2002", "second@example.test");

        AccountAuthException failure;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failure = await Should.ThrowAsync<AccountAuthException>(() => service.SignInAsync(CancellationToken.None));
            File.Exists(path + ".bad").ShouldBeFalse();
        }

        failure.Code.ShouldBe(AuthErrorCodes.AccountsFileLocked);
        AuthErrorCodes.ActionResourceKey(failure.Code).ShouldBe("AuthAction_TryAgain");
        File.ReadAllBytes(path).ShouldBe(original);

        clock.Advance(TimeSpan.FromSeconds(3));
        await service.SignInAsync(CancellationToken.None);

        service.Accounts.Select(a => a.Email).ShouldBe(["first@example.test", "second@example.test"], ignoreOrder: true);
        first.CurrentAccount.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_token_request_while_the_accounts_file_is_locked_says_locked_not_sign_in_again()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var restarted = harness.NewService();

        using (new FileStream(Path.Combine(harness.AuthDirectory, "accounts.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Should.ThrowAsync<AccountAuthException>(() => restarted.GetAccessTokenAsync(CancellationToken.None));

            failure.Code.ShouldBe(AuthErrorCodes.AccountsFileLocked);
        }
    }

    [Fact]
    public async Task While_the_accounts_file_is_locked_a_read_is_not_retried_inside_the_window_and_is_after_it()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var clock = new ManualClock();
        harness.Clock = clock;
        var restarted = harness.NewService();

        using (var hold = new FileStream(Path.Combine(harness.AuthDirectory, "accounts.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            restarted.IsSignedIn.ShouldBeFalse("the file could not be read");
            hold.Dispose();
            restarted.IsSignedIn.ShouldBeFalse("inside the window the file is not touched again, so the release is not seen yet");

            clock.Advance(TimeSpan.FromSeconds(3));
            restarted.IsSignedIn.ShouldBeTrue("after the window the real file is read");
        }
    }

    [Fact]
    public async Task Signing_out_while_the_accounts_file_is_locked_says_locked_and_changes_nothing()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var restarted = harness.NewService();

        using (new FileStream(Path.Combine(harness.AuthDirectory, "accounts.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Should.ThrowAsync<AccountAuthException>(() => restarted.SignOutAsync(CancellationToken.None));

            failure.Code.ShouldBe(AuthErrorCodes.AccountsFileLocked);
        }

        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue("the token was not deleted");
    }

    [Fact]
    public async Task Choosing_a_project_over_a_damaged_file_also_keeps_it()
    {
        using var harness = new AuthHarness();
        Directory.CreateDirectory(harness.AuthDirectory);
        var path = Path.Combine(harness.AuthDirectory, "accounts.json");
        File.WriteAllBytes(path, Damaged);
        var service = harness.NewService();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SelectProjectAsync("deg-proj-one", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.AccountsFileUnreadable);
        File.ReadAllBytes(path + ".bad").ShouldBe(Damaged);
    }
}
