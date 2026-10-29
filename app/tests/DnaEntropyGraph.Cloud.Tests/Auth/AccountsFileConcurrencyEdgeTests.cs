using System.Text;
using System.Text.Json;
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
        File.Exists(Path.Combine(harness.AuthDirectory, "1001.tok")).ShouldBeTrue("the sign-out is mid-revoke: its token file is not deleted yet");

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
    public async Task A_sign_in_that_set_a_damaged_file_aside_is_followed_by_the_notice_on_the_next_change()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        File.WriteAllBytes(AccountsPath(harness), Damaged);
        harness.Google.NextIdentity = new FakeIdentity("2002", "second@example.test");

        await service.SignInAsync(CancellationToken.None);
        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SelectProjectAsync("deg-proj-one", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.AccountsFileUnreadable);
        File.ReadAllBytes(AccountsPath(harness) + ".bad").ShouldBe(Damaged);
        await service.SelectProjectAsync("deg-proj-one", CancellationToken.None);
        service.SelectedProjectId.ShouldBe("deg-proj-one");
    }
}
