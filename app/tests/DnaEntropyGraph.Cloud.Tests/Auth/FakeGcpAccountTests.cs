using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

public class FakeGcpAccountTests
{
    [Fact]
    public async Task Sign_in_out_and_switch_move_CurrentAccount_and_raise_the_event()
    {
        var gcp = new FakeGcp();
        var changes = 0;
        gcp.AccountChanged += (_, _) => changes++;
        gcp.CurrentAccount.ShouldBeNull();

        await gcp.SignInAsync(CancellationToken.None);
        gcp.CurrentAccount.ShouldNotBeNull();
        gcp.Accounts.Count.ShouldBe(1);
        await gcp.SwitchAccountAsync(gcp.CurrentAccount!.Sub, CancellationToken.None);
        (await gcp.SignOutAsync(CancellationToken.None)).ShouldBeTrue();

        gcp.IsSignedIn.ShouldBeFalse();
        gcp.Accounts.ShouldBeEmpty();
        changes.ShouldBe(3);
    }

    [Fact]
    public async Task A_scripted_sign_in_error_throws_the_code_and_a_switch_to_a_stranger_is_refused()
    {
        var gcp = new FakeGcp().WithSignInError(AuthErrorCodes.SigninCancelled);

        (await Should.ThrowAsync<AccountAuthException>(() => gcp.SignInAsync(CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.SigninCancelled);
        gcp.IsSignedIn.ShouldBeFalse();
        (await Should.ThrowAsync<AccountAuthException>(() => gcp.SwitchAccountAsync("nobody", CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.AccountNotFound);
    }
}
