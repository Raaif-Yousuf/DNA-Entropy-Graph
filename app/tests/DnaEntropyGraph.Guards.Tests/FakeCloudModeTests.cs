using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #69: <c>--fake-cloud</c> / <c>DEG_FAKE_CLOUD=1</c> is the one switch back to the simulated cloud now that production
/// resolves the real gateways (#609), and <c>--fresh-profile</c> gives the run an empty throwaway data folder.
/// </summary>
public sealed class FakeCloudModeTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "deg-fakecloud-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static string? NoEnv(string name) => null;

    [Fact]
    public void No_flag_and_no_variable_is_the_real_cloud()
    {
        FakeCloudMode.BackendFor(["--profile", "x"], NoEnv).ShouldBe(CloudBackend.Google);
    }

    [Fact]
    public void The_flag_selects_the_developer_fake_cloud()
    {
        FakeCloudMode.BackendFor(["--fake-cloud"], NoEnv).ShouldBe(CloudBackend.Developer);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void The_variable_selects_the_developer_fake_cloud(string value)
    {
        FakeCloudMode.BackendFor([], name => name == "DEG_FAKE_CLOUD" ? value : null).ShouldBe(CloudBackend.Developer);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("no")]
    public void Any_other_variable_value_is_the_real_cloud(string value)
    {
        FakeCloudMode.BackendFor([], name => name == "DEG_FAKE_CLOUD" ? value : null).ShouldBe(CloudBackend.Google);
    }

    [Fact]
    public void Fresh_profile_adds_a_new_empty_profile_folder_each_time()
    {
        var a = FakeCloudMode.WithFreshProfile(["--fresh-profile"], _scratch);
        var b = FakeCloudMode.WithFreshProfile(["--fresh-profile"], _scratch);

        a[0].ShouldBe("--profile");
        a[1].ShouldStartWith(_scratch);
        a[1].ShouldNotBe(b[1]);
        a.ShouldNotContain("--fresh-profile");
    }

    [Fact]
    public void An_explicit_profile_wins_over_fresh_profile()
    {
        var args = FakeCloudMode.WithFreshProfile(["--profile", "mine", "--fresh-profile"], _scratch);

        args.ShouldBe(["--profile", "mine"]);
    }

    [Fact]
    public void Without_the_flag_the_arguments_are_untouched()
    {
        FakeCloudMode.WithFreshProfile(["--fake-cloud"], _scratch).ShouldBe(["--fake-cloud"]);
    }

    [Fact]
    public async Task The_developer_backend_is_the_connected_fake_account_and_signs_in_with_no_browser_and_no_network()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _scratch, cloud: CloudBackend.Developer);
        await using var provider = services.BuildServiceProvider();

        var account = provider.GetRequiredService<IGcpAccount>();
        account.ShouldBeOfType<FakeGcp>();
        await account.SignInAsync(CancellationToken.None);

        account.IsSignedIn.ShouldBeTrue();
        account.SelectedProjectId.ShouldNotBeNullOrWhiteSpace();
        provider.GetRequiredService<IWorkerImageProvider>().Resolve("0.0.0", gpu: true).Status.ShouldBe(WorkerImageStatus.Available);
        // Connected: the preflight answers instead of cloud_not_connected.
        (await provider.GetRequiredService<IProjectSetupGateway>().IsBillingEnabledAsync(account.SelectedProjectId!, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public void An_explicit_profile_in_the_equals_form_also_wins_over_fresh_profile()
    {
        var args = FakeCloudMode.WithFreshProfile(["--profile=C:\\mine", "--fresh-profile"], _scratch);

        args.ShouldBe(["--profile=C:\\mine"]);
    }

    [Fact]
    public void Resolve_flips_the_backend_the_app_then_builds_its_container_over()
    {
        // The init-order observable: Program.Main calls Resolve, and only afterwards App's constructor reads Backend into the container.
        try
        {
            FakeCloudMode.Resolve(["--FAKE-CLOUD", "--fresh-profile"]).ShouldContain("--profile");
            FakeCloudMode.Backend.ShouldBe(CloudBackend.Developer);

            var services = new ServiceCollection();
            services.AddDnaEntropyGraph(appDataRoot: _scratch, cloud: FakeCloudMode.Backend);
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IGcpAccount>().ShouldBeOfType<FakeGcp>();
        }
        finally
        {
            FakeCloudMode.Resolve([]);
        }

        FakeCloudMode.Backend.ShouldBe(CloudBackend.Google);
    }

    [Fact]
    public void A_variable_with_spaces_around_the_one_still_counts()
    {
        FakeCloudMode.BackendFor([], name => name == "DEG_FAKE_CLOUD" ? " 1 " : null).ShouldBe(CloudBackend.Developer);
    }

    [Fact]
    public void The_flag_is_case_insensitive()
    {
        FakeCloudMode.BackendFor(["--FAKE-CLOUD"], NoEnv).ShouldBe(CloudBackend.Developer);
    }

    [Fact]
    public void A_simulated_cloud_is_refused_on_the_real_profile_and_allowed_on_a_sandbox()
    {
        // DECISION (agent-made, reversible): fake runs must never write into the real history, so the simulated cloud needs a sandbox folder.
        FakeCloudMode.IsAllowedOn(CloudBackend.Developer, AppDataRoot.Default()).ShouldBeFalse();
        FakeCloudMode.IsAllowedOn(CloudBackend.Developer, AppDataRoot.FromPath(_scratch)).ShouldBeTrue();
        FakeCloudMode.IsAllowedOn(CloudBackend.Google, AppDataRoot.Default()).ShouldBeTrue();
    }

    [Fact]
    public async Task The_developer_container_has_no_real_account_service_and_everything_still_builds()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _scratch, cloud: CloudBackend.Developer);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        provider.GetService<DnaEntropyGraph.Cloud.Auth.GoogleAccountService>().ShouldBeNull("nothing may sit underneath the fake account");
        (await provider.GetRequiredService<IGcpAccessTokenSource>().GetAccessTokenAsync(null, CancellationToken.None)).ShouldNotBeNullOrEmpty();
        provider.GetRequiredService<DnaEntropyGraph.Presentation.ViewModels.ShellViewModel>().HasSimulatedCloud.ShouldBeTrue();
    }

    [Fact]
    public void The_shell_of_a_real_build_shows_no_simulated_cloud_banner()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _scratch, cloud: CloudBackend.Google);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<DnaEntropyGraph.Presentation.ViewModels.ShellViewModel>().HasSimulatedCloud.ShouldBeFalse();
    }

    [Fact]
    public void The_shell_binds_the_simulated_cloud_banner()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "MainWindow.xaml"));

        xaml.ShouldContain("ViewModel.HasSimulatedCloud");
        xaml.ShouldContain("ViewModel.SimulatedCloudBannerText");
    }
    [Fact]
    public void The_real_backend_still_resolves_no_fake()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _scratch, cloud: CloudBackend.Google);
        using var provider = services.BuildServiceProvider();

        provider.GetService<FakeGcp>().ShouldBeNull();
    }
}
