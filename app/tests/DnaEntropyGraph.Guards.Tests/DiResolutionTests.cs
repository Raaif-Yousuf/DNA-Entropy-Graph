using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App.Startup;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #61's own observable: "Registering a ViewModel without one of its
/// dependencies fails <c>dotnet test</c> naming the missing type." This
/// builds the exact same <c>IServiceCollection</c> the real app builds
/// (<see cref="ServiceRegistration.AddDnaEntropyGraph"/> in
/// DnaEntropyGraph.App - not a second, parallel container that could drift
/// from it) so a missing registration fails here, in seconds, rather than
/// on a user's first page open.
/// </summary>
public class DiResolutionTests
{
    private static ServiceProvider BuildRealServiceProvider()
    {
        var services = new ServiceCollection();

        // A throwaway temp directory, never the real
        // %LOCALAPPDATA%\DNAEntropyGraph\: ValidateOnBuild below eagerly
        // constructs every singleton, including SqliteDatabase, whose
        // constructor really does create its parent directory - a guard
        // test must not leave that behind on the real machine it runs on
        // (MEASURED 2026-09-19: it did, before this fix - see
        // ServiceRegistration.AddDnaEntropyGraph's own doc comment).
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value);

        // ValidateOnBuild walks every registration eagerly and throws
        // immediately, naming the exact missing type, instead of waiting
        // for whichever ViewModel happens to need it first.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// One temp directory per test run (not per call - several tests in
    /// this file each build their own provider), deleted when the process
    /// exits via <see cref="AppDomain.ProcessExit"/> since no single test
    /// owns "last" here and a shared xunit fixture would be more ceremony
    /// than this warrants for a directory that never holds more than an
    /// empty folder (see the doc comment on <c>AddDnaEntropyGraph</c>: DI
    /// validation alone never opens the database or writes settings.json).
    /// </summary>
    private static class TempAppDataRoot
    {
        public static string Value { get; } = CreateAndRegisterCleanup();

        private static string CreateAndRegisterCleanup()
        {
            var path = Path.Combine(Path.GetTempPath(), $"deg-guard-appdata-{Guid.NewGuid():n}");
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            };
            return path;
        }
    }

    [Fact]
    public void Every_registered_ViewModel_resolves_with_no_missing_dependency()
    {
        using var provider = BuildRealServiceProvider();

        var viewModelTypes = typeof(DnaEntropyGraph.Presentation.ViewModels.ShellViewModel).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("ViewModel", StringComparison.Ordinal))
            .ToList();

        // Vacuity check (wired-to-nothing: "a guard test that passes
        // because the directory it scans is empty" is not a pass).
        viewModelTypes.ShouldNotBeEmpty();
        viewModelTypes.Count.ShouldBe(
            9,
            "docs/superpowers/specs Appendix A section 1 names exactly 9 ViewModels; " +
            "update this count deliberately alongside that table, not by accident.");

        foreach (var viewModelType in viewModelTypes)
        {
            provider.GetRequiredService(viewModelType).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Every_Presentation_facing_and_Cloud_gateway_interface_resolves()
    {
        using var provider = BuildRealServiceProvider();

        Type[] interfaces =
        [
            typeof(DnaEntropyGraph.Core.Abstractions.IJobEngine),
            typeof(DnaEntropyGraph.Core.Abstractions.IRunRepository),
            typeof(DnaEntropyGraph.Core.Abstractions.ISettingsStore),
            typeof(DnaEntropyGraph.Core.Abstractions.IGcpAccount),
            typeof(DnaEntropyGraph.Core.Abstractions.IDispatcher),
            typeof(DnaEntropyGraph.Core.Abstractions.IFilePicker),
            typeof(DnaEntropyGraph.Core.Abstractions.IToastService),
            typeof(DnaEntropyGraph.Core.Abstractions.INavigator),
            typeof(DnaEntropyGraph.Core.Abstractions.IDialogService),
            typeof(DnaEntropyGraph.Presentation.Services.IStringResourceProvider),
            typeof(DnaEntropyGraph.Presentation.Services.IRunVmActions),
            typeof(DnaEntropyGraph.Presentation.Services.ILogTailReader),
            typeof(IMessenger),
            typeof(DnaEntropyGraph.Core.Cloud.IComputeGateway),
            typeof(DnaEntropyGraph.Core.Cloud.IStorageGateway),
            typeof(DnaEntropyGraph.Core.Cloud.IProjectSetupGateway),
            typeof(DnaEntropyGraph.Core.Cloud.IQuotaGateway),
        ];

        foreach (var serviceType in interfaces)
        {
            provider.GetRequiredService(serviceType).ShouldNotBeNull();
        }
    }

    [Fact]
    public void CloudJobRunner_is_registered_and_JobEngine_is_the_same_instance_behind_both_interfaces()
    {
        using var provider = BuildRealServiceProvider();

        // Issue #428: a runner nothing constructs is the wired-to-nothing bug.
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.CloudJobRunner>().ShouldNotBeNull();
        provider.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IJobEngine>()
            .ShouldBeSameAs(provider.GetRequiredService<DnaEntropyGraph.Presentation.Services.IRunVmActions>());
    }

    [Fact]
    public async Task Gateways_resolved_from_the_real_container_retry_transient_failures()
    {
        // Issue #258's observable, through the production registration: two
        // 503s then success completes with two retries logged. If the
        // gateways were registered without the resilience wrapper, the first
        // 503 would surface as an exception here.
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value);
        services.AddSingleton(new DnaEntropyGraph.Cloud.CloudRetryOptions { BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero });
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<DnaEntropyGraph.Cloud.FakeGcp>().WithTransientFailures(503, 2);

        var enabled = await provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IProjectSetupGateway>()
            .IsBillingEnabledAsync("my-project", CancellationToken.None);

        enabled.ShouldBeTrue();
        provider.GetRequiredService<DnaEntropyGraph.Cloud.CloudRetryLog>().Retries.Count.ShouldBe(2);
    }

    [Fact]
    public void The_real_production_container_builds_with_no_missing_registration()
    {
        Should.NotThrow(() =>
        {
            using var provider = BuildRealServiceProvider();
        });
    }
}
