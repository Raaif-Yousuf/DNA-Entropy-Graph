using System.Text.Json;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>Issue #520: the chosen project is part of the account record, so it follows the account and dies with it.</summary>
public class ProjectSelectionTests
{
    private const string Project = "deg-proj-one";

    [Fact]
    public async Task A_chosen_project_is_read_back_survives_a_restart_and_is_written_in_the_account_record()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        service.SelectedProjectId.ShouldBeNull();

        await service.SelectProjectAsync(Project, CancellationToken.None);

        service.SelectedProjectId.ShouldBe(Project);
        harness.NewService().SelectedProjectId.ShouldBe(Project);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.AuthDirectory, "accounts.json")));
        document.RootElement.GetProperty("accounts")[0].GetProperty("projectId").GetString().ShouldBe(Project);
    }

    [Fact]
    public async Task Choosing_a_project_raises_AccountChanged()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        var changes = 0;
        service.AccountChanged += (_, _) => changes++;

        await service.SelectProjectAsync(Project, CancellationToken.None);

        changes.ShouldBe(1);
    }

    [Fact]
    public async Task The_project_follows_the_account_when_switching_back_and_forth()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        await service.SelectProjectAsync(Project, CancellationToken.None);
        await harness.SignedInAsync("2002", "second@example.test", service);

        service.SelectedProjectId.ShouldBeNull("the second account has chosen nothing");
        await service.SelectProjectAsync("deg-proj-two", CancellationToken.None);
        await service.SwitchAccountAsync("1001", CancellationToken.None);
        service.SelectedProjectId.ShouldBe(Project);
        await service.SwitchAccountAsync("2002", CancellationToken.None);
        service.SelectedProjectId.ShouldBe("deg-proj-two");
        harness.NewService().SelectedProjectId.ShouldBe("deg-proj-two");
    }

    [Fact]
    public async Task Signing_out_drops_that_accounts_project_and_a_new_sign_in_starts_with_none()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        await service.SelectProjectAsync(Project, CancellationToken.None);

        await service.SignOutAsync(CancellationToken.None);

        service.SelectedProjectId.ShouldBeNull();
        File.ReadAllText(Path.Combine(harness.AuthDirectory, "accounts.json")).ShouldNotContain(Project);
        (await harness.SignedInAsync("1001", "first@example.test", service)).SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public async Task Signing_in_again_to_an_account_that_expired_keeps_its_project()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        await service.SelectProjectAsync(Project, CancellationToken.None);
        File.Delete(Path.Combine(harness.AuthDirectory, "1001.tok"));
        var restarted = harness.NewService();
        restarted.IsSignedIn.ShouldBeFalse();

        await harness.SignedInAsync("1001", "first@example.test", restarted);

        restarted.SelectedProjectId.ShouldBe(Project);
    }

    [Fact]
    public async Task An_accounts_file_written_before_projects_existed_loads_with_no_project()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        File.WriteAllText(
            Path.Combine(harness.AuthDirectory, "accounts.json"),
            "{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"email\":\"first@example.test\",\"needsSignIn\":false}]}");

        var service = harness.NewService();

        service.IsSignedIn.ShouldBeTrue();
        service.SelectedProjectId.ShouldBeNull();
        await service.SelectProjectAsync(Project, CancellationToken.None);
        harness.NewService().SelectedProjectId.ShouldBe(Project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Has-Capitals")]
    [InlineData("ab")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("has space")]
    public async Task An_invalid_project_id_is_PROJECT_INVALID_and_changes_nothing(string projectId)
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        await service.SelectProjectAsync(Project, CancellationToken.None);

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SelectProjectAsync(projectId, CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.ProjectInvalid);
        service.SelectedProjectId.ShouldBe(Project);
    }

    [Fact]
    public async Task Choosing_a_project_with_nobody_signed_in_says_sign_in()
    {
        using var harness = new AuthHarness();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => harness.NewService().SelectProjectAsync(Project, CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninExpired);
    }

    [Fact]
    public async Task The_fallback_answers_only_while_signed_in_and_only_until_a_project_is_chosen()
    {
        using var harness = new AuthHarness { FallbackProjectId = "fallback-project" };
        var service = harness.NewService();
        service.SelectedProjectId.ShouldBeNull("signed out stays null");

        await harness.SignedInAsync("1001", "first@example.test", service);
        service.SelectedProjectId.ShouldBe("fallback-project");

        await service.SelectProjectAsync(Project, CancellationToken.None);
        service.SelectedProjectId.ShouldBe(Project);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_is_SIGNIN_STORAGE_and_the_choice_is_not_kept()
    {
        using var harness = new AuthHarness();
        var service = await harness.SignedInAsync("1001", "first@example.test");
        var accounts = Path.Combine(harness.AuthDirectory, "accounts.json");
        File.Delete(accounts);
        Directory.CreateDirectory(accounts);

        var failure = await Should.ThrowAsync<AccountAuthException>(() => service.SelectProjectAsync(Project, CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.StorageFailed);
        service.SelectedProjectId.ShouldBeNull();
    }
}
