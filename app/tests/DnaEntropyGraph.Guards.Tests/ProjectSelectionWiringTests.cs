using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #520's "wired to nothing" check against the real container: a project chosen through the wizard's command is what
/// <see cref="IGcpAccount.SelectedProjectId"/> answers, it is still there after a restart (a new container over the same
/// folder) and it follows the account when the user switches away and back.
/// </summary>
public class ProjectSelectionWiringTests
{
    private static ServiceProvider Build(string root)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: root);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_project_chosen_in_the_wizard_is_what_the_account_answers_survives_a_restart_and_follows_the_account()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the seeded token files are DPAPI-protected");
        var root = Path.Combine(Path.GetTempPath(), $"deg-guard-project-{Guid.NewGuid():n}");
        var auth = Path.Combine(root, "auth");
        Directory.CreateDirectory(auth);
        try
        {
            var protector = new DnaEntropyGraph.Cloud.Auth.DpapiSecretProtector();
            foreach (var sub in new[] { "1001", "2002" })
            {
                File.WriteAllBytes(Path.Combine(auth, sub + ".tok"), protector.Protect(System.Text.Encoding.UTF8.GetBytes("{\"access_token\":\"a\",\"refresh_token\":\"r\"}")));
            }

            File.WriteAllText(
                Path.Combine(auth, "accounts.json"),
                "{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"email\":\"first@example.test\",\"needsSignIn\":false},{\"sub\":\"2002\",\"email\":\"second@example.test\",\"needsSignIn\":false}]}");

            await using (var provider = Build(root))
            {
                var account = provider.GetRequiredService<IGcpAccount>();
                account.SelectedProjectId.ShouldBe("fake-project", "the stopgap answers until a project is chosen");

                await provider.GetRequiredService<WizardViewModel>().ChooseProjectCommand.ExecuteAsync("first-project");

                account.SelectedProjectId.ShouldBe("first-project");
                await account.SwitchAccountAsync("2002", CancellationToken.None);
                account.SelectedProjectId.ShouldBe("fake-project", "the second account chose nothing");
                await account.SwitchAccountAsync("1001", CancellationToken.None);
                account.SelectedProjectId.ShouldBe("first-project");
            }

            await using var restarted = Build(root);
            restarted.GetRequiredService<IGcpAccount>().SelectedProjectId.ShouldBe("first-project");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
