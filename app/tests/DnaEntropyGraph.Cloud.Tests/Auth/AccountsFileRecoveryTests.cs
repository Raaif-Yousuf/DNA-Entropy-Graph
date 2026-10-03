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
