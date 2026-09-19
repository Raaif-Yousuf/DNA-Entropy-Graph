using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.LocalEngine;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.Services;
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
    /// <summary>
    /// <paramref name="appDataRoot"/> lets a caller redirect
    /// <see cref="SqliteDatabase"/> and <see cref="SettingsStore"/> away
    /// from the real <c>%LOCALAPPDATA%\DNAEntropyGraph\</c> - the real app
    /// never passes it (<c>null</c> means "use the real default paths"),
    /// but <c>Guards.Tests/DiResolutionTests</c> does, to a throwaway temp
    /// directory it deletes afterward. Without this, building this same
    /// graph under <c>ValidateOnBuild</c> - which eagerly constructs every
    /// singleton, including <see cref="SqliteDatabase"/>, to prove the DI
    /// graph resolves - has a real side effect on the machine running the
    /// test: <see cref="SqliteDatabase"/>'s constructor creates its parent
    /// directory for real. MEASURED 2026-09-19: running the guard suite
    /// left an empty <c>%LOCALAPPDATA%\DNAEntropyGraph\</c> on this
    /// machine; no <c>app.db</c> or <c>settings.json</c> is written, since
    /// neither constructor opens a connection or touches the settings
    /// file - only <c>OpenConnection</c>/<c>GetString</c>/<c>SetString</c>
    /// do that, and DI validation never calls them - but the directory
    /// itself is a real, unsandboxed write a guard test should not make.
    /// </summary>
    public static IServiceCollection AddDnaEntropyGraph(this IServiceCollection services, string? appDataRoot = null)
    {
        var databasePath = appDataRoot is null ? SqliteDatabase.DefaultPath() : Path.Combine(appDataRoot, "app.db");
        var settingsPath = appDataRoot is null ? SettingsStore.DefaultPath() : Path.Combine(appDataRoot, "settings.json");

        // App-owned, WinUI-bound services (Presentation depends on their
        // interfaces only - docs/architecture.md section 2).
        services.AddSingleton<IDispatcher, DispatcherAdapter>();
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigator>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<IToastService, ToastService>();
        services.AddSingleton<IFilePicker, FilePickerService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IStringResourceProvider, ReswStringResourceProvider>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<ILogTailReader, FileLogTailReader>();

        // Cloud (Hard Rule 7: the only project allowed to reference
        // Google.*). FakeGcp is the always-succeeds baseline until the real
        // gateways land; it backs every Core/Cloud interface at once.
        services.AddSingleton<FakeGcp>();
        services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IComputeGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IStorageGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IProjectSetupGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IQuotaGateway>(sp => sp.GetRequiredService<FakeGcp>());

        // Persistence (issue #67: real SQLite + settings.json under
        // %LOCALAPPDATA%\DNAEntropyGraph\, docs/architecture.md section 6).
        // SqliteDatabase and SettingsStore both take a file path, so each
        // gets a factory registration rather than the type-to-type shorthand
        // the rest of this method uses.
        services.AddSingleton(_ => new SqliteDatabase(databasePath));
        services.AddSingleton<IRunRepository>(sp => new RunRepository(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<ISettingsStore>(_ => new SettingsStore(settingsPath));

        // LocalEngine.
        services.AddSingleton<LocalEngineManager>();
        services.AddSingleton<RunTargetResolver>();

        // App-owned singleton that survives navigation (docs/architecture.md section 3).
        // Registered once as its own concrete type and exposed through both
        // interfaces it implements, the same multi-interface-single-instance
        // pattern FakeGcp uses above, so RunProgressViewModel's IRunVmActions
        // and IJobEngine consumers always see the same tracked run state.
        services.AddSingleton<JobEngine>();
        services.AddSingleton<IJobEngine>(sp => sp.GetRequiredService<JobEngine>());
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IRunVmActions>(sp => sp.GetRequiredService<JobEngine>());

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
