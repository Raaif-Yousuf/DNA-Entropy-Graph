using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #644's wired-to-nothing observables, on the real container: what the Advanced group saves is what the run start reads.</summary>
public sealed class AdvancedSettingsWiringTests : IDisposable
{
    private const string Pinned = "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-advanced-" + Guid.NewGuid().ToString("N"));

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

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_worker_image_set_in_the_Advanced_group_is_the_image_the_next_run_resolves_and_Reset_removes_it()
    {
        using var provider = Build();
        var settings = provider.GetRequiredService<ISettingsStore>();
        settings.SetString(WorkerImageSettingKeys.DeveloperMode, "true");
        var vm = provider.GetRequiredService<AdvancedSettingsViewModel>();
        var images = provider.GetRequiredService<IWorkerImageProvider>();

        vm.WorkerImage = Pinned;

        var resolved = images.Resolve("0.0.1-nothing-shipped", gpu: true);
        resolved.Status.ShouldBe(WorkerImageStatus.Available);
        resolved.Reference.ShouldBe(Pinned);

        provider.GetRequiredService<IResettableSettings>().ResetAll();

        images.Resolve("0.0.1-nothing-shipped", gpu: true).Reference.ShouldBeNull();
    }

    [Theory]
    [InlineData(CloudBackend.Google)]
    [InlineData(CloudBackend.Fake)]
    [InlineData(CloudBackend.Developer)]
    public void The_container_says_the_cloud_is_simulated_exactly_when_the_backend_is_not_Google(CloudBackend backend)
    {
        // The rule, not today's state (#644 cold review 11): it follows the backend the container was built over, so it cannot
        // go stale when a gateway or the production default changes.
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: backend);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICloudModeInfo>().IsFakeCloud.ShouldBe(backend != CloudBackend.Google);
        provider.GetRequiredService<IResettableSettings>().ShouldBeSameAs(provider.GetRequiredService<ISettingsStore>());
    }
}
