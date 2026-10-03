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

    [Fact]
    public async Task SelectProjectAsync_sets_the_project_validates_the_id_and_needs_a_signed_in_account()
    {
        var gcp = new FakeGcp();
        (await Should.ThrowAsync<AccountAuthException>(() => gcp.SelectProjectAsync("", CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.ProjectInvalid, "the id is checked first, as the real service does");
        (await Should.ThrowAsync<AccountAuthException>(() => gcp.SelectProjectAsync("my-project-1", CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.SigninExpired);

        await gcp.SignInAsync(CancellationToken.None);
        var changes = 0;
        gcp.AccountChanged += (_, _) => changes++;
        await gcp.SelectProjectAsync("my-project-1", CancellationToken.None);

        gcp.SelectedProjectId.ShouldBe("my-project-1");
        changes.ShouldBe(1);
        (await Should.ThrowAsync<AccountAuthException>(() => gcp.SelectProjectAsync("", CancellationToken.None))).Code.ShouldBe(AuthErrorCodes.ProjectInvalid);
        gcp.SelectedProjectId.ShouldBe("my-project-1");
    }
}
