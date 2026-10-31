using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #830 review M3, through the production wiring: a finished run made under a Google account that is signed out is found by the launch
/// pass (<see cref="AppStartup.BeginAsync"/>), and the shell's "Sign in as ..." bar (<see cref="SignInNeededViewModel"/>, resolved from the real container) opens
/// naming that account. A reconciler whose outcome is never handed to the bar leaves it closed here.
/// </summary>
public sealed class SignInNeededStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-signin-needed-" + Guid.NewGuid().ToString("N"));

    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment.
        }
    }

    [Fact]
    public async Task A_run_whose_account_is_signed_out_opens_the_shells_sign_in_bar_naming_the_account()
    {
        // Review F4: the bar is for an account with something that could still be running: here a finished run whose computer end was not verified.
        var bar = await LaunchWithSignedOutAccountAsync(ErrorCode: RunErrorCodes.LifecycleUnverified);
        bar.IsOpen.ShouldBeTrue("the launch pass could not look at job-a (its account is signed out) and the shell must say so");
        bar.Title.ShouldContain("a@example.org");
    }

    [Fact]
    public async Task A_signed_out_account_with_nothing_that_could_still_be_running_does_not_open_the_bar()
    {
        var bar = await LaunchWithSignedOutAccountAsync(ErrorCode: null);
        bar.IsOpen.ShouldBeFalse("a Completed run whose computer was confirmed ended has nothing to protect");
    }

    private async Task<SignInNeededViewModel> LaunchWithSignedOutAccountAsync(string? ErrorCode)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        services.AddSingleton<FakeGcp>(_ => new FakeGcp()
            .WithSelectedProject("my-project")
            .WithAccount("sub-a", "a@example.org", "project-a")
            .WithAccountSignedOut("sub-a"));
        services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<FakeGcp>());
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        using (var connection = provider.GetRequiredService<DnaEntropyGraph.Persistence.SqliteDatabase>().OpenConnection())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO Projects (ProjectId) VALUES ('project-a')";
            insert.ExecuteNonQuery();
        }

        await provider.GetRequiredService<IRunRepository>().UpsertAsync(
            new RunRecord(
                "job-a",
                JobPhase.Completed,
                DateTimeOffset.UtcNow.AddMinutes(-30),
                Target: "cloud",
                ProjectId: "project-a",
                AccountSub: "sub-a",
                InstallationId: InstallationId.GetOrCreate(provider.GetRequiredService<ISettingsStore>()),
                FinishedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
                ErrorCode: ErrorCode),
            CancellationToken.None);

        await AppStartup.BeginAsync(provider, CancellationToken.None);

        return provider.GetRequiredService<SignInNeededViewModel>();
    }

    [Fact]
    public void The_run_page_sign_in_prompt_copy_fits_both_stop_and_delete()
    {
        // Issue #830 review M2 (e): the same prompt follows a failed Stop and a failed Delete, so neither word may be missing from it.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in System.Xml.Linq.XDocument.Load(file).Descendants("data"))
            {
                values[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        values["RunSignIn_Prompt"].ShouldContain("stop or delete");
        values["RunSignIn_PromptUnknown"].ShouldContain("stop or delete");
    }
}
