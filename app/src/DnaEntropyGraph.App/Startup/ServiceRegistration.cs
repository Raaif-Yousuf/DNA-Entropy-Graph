using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.LocalEngine;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// The single, real production DI graph. <c>App.xaml.cs</c> calls this from
/// its constructor and nothing else that branches (Hard Rule 8); Guards.Tests
/// calls the exact same method to build the exact same graph with no WinUI
/// window ever created, which is what makes
/// Guards.Tests/DiResolutionTests a test of the real wiring rather than of a
/// second, parallel container that could silently drift from the one the
/// app actually runs (see wired-to-nothing: "parallel slices that each
/// assume a sibling wires them together").
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddDnaEntropyGraph(this IServiceCollection services)
    {
        // App-owned, WinUI-bound services (Presentation depends on their
        // interfaces only - docs/architecture.md section 2).
        services.AddSingleton<IDispatcher, DispatcherAdapter>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigator>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<IToastService, ToastService>();
        services.AddSingleton<IFilePicker, FilePickerService>();
        services.AddSingleton<IDialogService, DialogService>();

        // Cloud (Hard Rule 7: the only project allowed to reference
        // Google.*). FakeGcp is the always-succeeds baseline until the real
        // gateways land; it backs every Core/Cloud interface at once.
        services.AddSingleton<FakeGcp>();
        services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IComputeGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IStorageGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IProjectSetupGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IQuotaGateway>(sp => sp.GetRequiredService<FakeGcp>());

        // Persistence (in-memory placeholders - see that project's csproj comment).
        services.AddSingleton<IRunRepository, RunRepository>();
        services.AddSingleton<ISettingsStore, SettingsStore>();

        // LocalEngine.
        services.AddSingleton<LocalEngineManager>();
        services.AddSingleton<RunTargetResolver>();

        // App-owned singleton that survives navigation (docs/architecture.md section 3).
        services.AddSingleton<IJobEngine, JobEngine>();

        // Every ViewModel (docs/superpowers/specs Appendix A section 1's
        // key-classes table). Transient: a fresh instance per page
        // navigation, per WinUI convention.
        services.AddTransient<ShellViewModel>();
        services.AddTransient<WizardViewModel>();
        services.AddTransient<NewRunViewModel>();
        services.AddTransient<RunProgressViewModel>();
        services.AddTransient<ResultsViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<CloudResourcesViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services;
    }
}
