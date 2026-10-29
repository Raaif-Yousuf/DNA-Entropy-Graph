using System.Text.Json;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>
/// Issue #620: two copies of the app (two service instances over one folder) each change a different part of <c>accounts.json</c>;
/// both changes must be in the file. A copy's own cached list can be stale, so a change is applied to the file as it is under the lock.
/// Same-account rule (documented in docs/cloud_design.md): each change touches only its own fields of its own account, so the later writer
/// wins per field and never undoes a field it did not change.
/// </summary>
public class AccountsFileMergeTests
{
    private const string ProjectOne = "deg-proj-one";
    private const string ProjectTwo = "deg-proj-two";

    private static JsonElement ReadFile(AuthHarness harness)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.AuthDirectory, "accounts.json")));
        return document.RootElement.Clone();
    }

    private static JsonElement Account(JsonElement file, string sub)
        => file.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("sub").GetString() == sub);

    [Fact]
    public async Task A_sign_in_in_one_copy_and_a_project_choice_in_the_other_both_end_up_in_the_file()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        copyB.IsSignedIn.ShouldBeTrue("copy B has now read, and cached, a list of one account");

        await harness.SignedInAsync("2002", "second@example.test", harness.NewService());
        await copyB.SelectProjectAsync(ProjectOne, CancellationToken.None);

        var file = ReadFile(harness);
        file.GetProperty("accounts").GetArrayLength().ShouldBe(2, "copy B's stale list must not erase the account copy A added");
        Account(file, "1001").GetProperty("projectId").GetString().ShouldBe(ProjectOne);
        Account(file, "2002").GetProperty("email").GetString().ShouldBe("second@example.test");
        file.GetProperty("activeSub").GetString().ShouldBe("2002", "a project choice does not change which account is current");
        var restarted = harness.NewService();
        restarted.Accounts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_sign_in_after_a_project_choice_elsewhere_keeps_the_chosen_project()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        copyB.IsSignedIn.ShouldBeTrue();

        var copyA = harness.NewService();
        await copyA.SelectProjectAsync(ProjectOne, CancellationToken.None);
        await harness.SignedInAsync("1001", "first@example.test", copyB);

        Account(ReadFile(harness), "1001").GetProperty("projectId").GetString().ShouldBe(ProjectOne);
    }

    [Fact]
    public async Task Two_project_choices_for_one_account_the_later_writer_wins()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        copyB.IsSignedIn.ShouldBeTrue();

        await harness.NewService().SelectProjectAsync(ProjectOne, CancellationToken.None);
        await copyB.SelectProjectAsync(ProjectTwo, CancellationToken.None);

        Account(ReadFile(harness), "1001").GetProperty("projectId").GetString().ShouldBe(ProjectTwo);
        harness.NewService().SelectedProjectId.ShouldBe(ProjectTwo);
    }

    [Fact]
    public async Task Signing_out_one_account_keeps_an_account_another_copy_added_and_its_current_choice()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        copyB.IsSignedIn.ShouldBeTrue();

        await harness.SignedInAsync("2002", "second@example.test", harness.NewService());
        await copyB.SignOutAsync(CancellationToken.None);

        var file = ReadFile(harness);
        file.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("sub").GetString()).ShouldBe(["2002"]);
        file.GetProperty("activeSub").GetString().ShouldBe("2002");
    }

    [Fact]
    public async Task A_project_choice_for_an_account_another_copy_signed_out_fails_and_writes_nothing()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var copyB = harness.NewService();
        copyB.IsSignedIn.ShouldBeTrue();

        await harness.NewService().SignOutAsync(CancellationToken.None);
        var failure = await Should.ThrowAsync<AccountAuthException>(() => copyB.SelectProjectAsync(ProjectOne, CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninExpired);
        ReadFile(harness).GetProperty("accounts").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_switch_in_one_copy_and_a_project_choice_in_the_other_both_persist()
    {
        using var harness = new AuthHarness();
        var first = await harness.SignedInAsync("1001", "first@example.test");
        await harness.SignedInAsync("2002", "second@example.test", first);
        var copyB = harness.NewService();
        copyB.CurrentAccount!.Sub.ShouldBe("2002");

        await harness.NewService().SwitchAccountAsync("1001", CancellationToken.None);
        await copyB.SelectProjectAsync(ProjectOne, CancellationToken.None);

        var file = ReadFile(harness);
        file.GetProperty("activeSub").GetString().ShouldBe("1001", "copy B's project choice did not touch which account is current");
        Account(file, "2002").GetProperty("projectId").GetString().ShouldBe(ProjectOne);
    }
}
