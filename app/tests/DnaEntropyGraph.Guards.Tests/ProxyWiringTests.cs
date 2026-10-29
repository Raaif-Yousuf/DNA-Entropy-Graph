using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core.Net;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #239, wired-to-nothing: the proxy policy exists, but nothing proves a client reads it unless the registration hands the SAME
/// object to every client's options and to the Settings page. A client built with no policy silently follows Windows only, which looks
/// right until a lab with a typed proxy says it does not work.
/// </summary>
public class ProxyWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-proxywiring-" + Guid.NewGuid().ToString("n"));

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void The_sign_in_and_the_google_gateways_are_handed_the_one_live_policy_the_settings_page_changes()
    {
        using var provider = Build();
        var policy = provider.GetRequiredService<ProxyPolicy>();

        provider.GetRequiredService<IProxyPolicy>().ShouldBeSameAs(policy);
        provider.GetRequiredService<GoogleAccountOptions>().Proxy.ShouldBeSameAs(policy);
        provider.GetRequiredService<GoogleCloudOptions>().Proxy.ShouldBeSameAs(policy);

        var page = provider.GetRequiredService<ProxySettingsViewModel>();
        page.SelectedModeIndex = 2;
        page.ApplyCommand.Execute(null);

        provider.GetRequiredService<GoogleCloudOptions>().Proxy!.Settings.Mode.ShouldBe(ProxyMode.None);
        provider.GetRequiredService<GoogleAccountOptions>().Proxy!.Settings.Mode.ShouldBe(ProxyMode.None);
    }

    [Fact]
    public void Every_github_source_the_updater_builds_carries_the_proxied_downloader()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Services", "VelopackUpdateSource.cs"));

        var sources = System.Text.RegularExpressions.Regex.Matches(source, @"new GithubSource\(").Count;
        var proxied = System.Text.RegularExpressions.Regex.Matches(source, @"new GithubSource\([^;]*new ProxiedFileDownloader\(proxy\)\)").Count;

        sources.ShouldBeGreaterThanOrEqualTo(2);
        proxied.ShouldBe(sources);
    }

    [Fact]
    public void The_ncbi_accession_lookup_is_built_over_the_proxy_policy()
    {
        var registration = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Startup", "ServiceRegistration.cs"));

        System.Text.RegularExpressions.Regex.IsMatch(registration, @"new NcbiAccessionLookup\(DnaEntropyGraph\.Cloud\.Net\.ProxiedHttpClient\.Create\(sp\.GetRequiredService<IProxyPolicy>\(\)").ShouldBeTrue();
    }

    [Fact]
    public void The_diagnostics_zip_is_given_the_policy_so_it_can_record_the_mode()
    {
        var registration = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Startup", "ServiceRegistration.cs"));

        System.Text.RegularExpressions.Regex.IsMatch(registration, @"DiagnosticsInfoProvider\.Current\([^;]*GetRequiredService<IProxyPolicy>\(\)\)").ShouldBeTrue();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
