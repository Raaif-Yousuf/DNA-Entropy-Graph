using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.LocalEngine;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
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
        // WithCloudNotConnected: nothing real is behind this yet, so a run must fail at preflight with
        // cloud_not_connected rather than "complete" with simulated results in the user's output folder
        // (cold review finding 12). The switch that turns the full simulation back on is issue #69.
        services.AddSingleton<FakeGcp>(_ => new FakeGcp().WithCloudNotConnected());
        // Issue #48: the account is the real Google sign-in (loopback + PKCE, DPAPI token files keyed by sub). The
        // gateways below stay FakeGcp until the real ones land, but who is signed in is already real. A build with no
        // oauth_client.local.json cannot sign in and says so by code (OAUTH_CLIENT_MISSING), never by crashing.
        // Every path is resolved lazily on first use, so building this graph (Guards.Tests) touches no disk.
        services.AddSingleton<GoogleAccountOptions>(sp => new GoogleAccountOptions
        {
            AuthDirectory = Path.Combine(Path.GetDirectoryName(settingsPath)!, "auth"),
            ClientLoader = new OAuthClientLoader(appDataRoot is null
                ? OAuthClientLoader.DefaultCandidates(Path.GetDirectoryName(settingsPath)!, AppContext.BaseDirectory)
                : [Path.Combine(appDataRoot, OAuthClientLoader.FileName)]),
            Browser = new SystemBrowserLauncher(),
            Pages = new LoopbackPages(
                () => sp.GetRequiredService<IStringResourceProvider>().GetString("SignInBrowserSuccess"),
                () => sp.GetRequiredService<IStringResourceProvider>().GetString("SignInBrowserFailure")),
        });
        services.AddSingleton<GoogleAccountService>();
        services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<GoogleAccountService>());
        services.AddSingleton<IGcpAccessTokenSource>(sp => sp.GetRequiredService<GoogleAccountService>());

        // Issue #258: every gateway the app resolves is wrapped in the one
        // resilience pipeline (retry with jitter on 429/5xx/transport, a
        // single token refresh on 401, a circuit breaker for the offline
        // banner). The real Google-backed gateways replace FakeGcp INSIDE
        // these wrappers. A test that needs fast retries registers its own
        // CloudRetryOptions after calling this method (the last one wins).
        services.AddSingleton<CloudRetryOptions>(_ => new CloudRetryOptions());
        services.AddSingleton<CloudRetryLog>();
        services.AddSingleton<ICloudCallObserver>(sp => sp.GetRequiredService<CloudRetryLog>());
        // The real account refreshes the real token on a 401. Inert while the gateways are FakeGcp (it never answers a
        // real 401), correct the moment a real gateway is wrapped, so the swap in #56 touches only the gateways.
        services.AddSingleton<ICloudTokenRefresher>(sp => sp.GetRequiredService<GoogleAccountService>());
        services.AddSingleton<CloudCallPipeline>(sp => new CloudCallPipeline(
            sp.GetRequiredService<CloudRetryOptions>(),
            sp.GetRequiredService<ICloudTokenRefresher>(),
            sp.GetRequiredService<ICloudCallObserver>()));
        services.AddSingleton<IComputeGateway>(sp => new ResilientComputeGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));
        services.AddSingleton<IStorageGateway>(sp => new ResilientStorageGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));
        services.AddSingleton<IProjectSetupGateway>(sp => new ResilientProjectSetupGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));
        services.AddSingleton<IQuotaGateway>(sp => new ResilientQuotaGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));

        // Persistence (issue #67: real SQLite + settings.json under
        // %LOCALAPPDATA%\DNAEntropyGraph\, docs/architecture.md section 6).
        // SqliteDatabase and SettingsStore both take a file path, so each
        // gets a factory registration rather than the type-to-type shorthand
        // the rest of this method uses.
        services.AddSingleton(_ => new SqliteDatabase(databasePath));
        services.AddSingleton<IRunRepository>(sp => new RunRepository(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<ISettingsStore>(_ => new SettingsStore(settingsPath));

        // Issue #460: the app's own copy of every run's input, under the same app data folder as the
        // database and settings (Hard Rule 14).
        services.AddSingleton<IRunInputStore>(_ => new LocalRunInputStore(Path.GetDirectoryName(settingsPath)!));

        // Issue #458: the worker image comes from the list pinned by digest that ships with the app.
        services.AddSingleton<PinnedWorkerImageList>(_ => PinnedWorkerImageProvider.LoadShippedList());
        services.AddSingleton<IWorkerImageProvider, PinnedWorkerImageProvider>();

        // LocalEngine.
        services.AddSingleton<LocalEngineManager>();
        services.AddSingleton<RunTargetResolver>();

        // App-owned singleton that survives navigation (docs/architecture.md section 3).
        // Registered once as its own concrete type and exposed through both
        // interfaces it implements, the same multi-interface-single-instance
        // pattern FakeGcp uses above, so RunProgressViewModel's IRunVmActions
        // and IJobEngine consumers always see the same tracked run state.
        // CloudJobRunner (issue #428): the state machine every cloud run goes
        // through. Its phase callback publishes the message ShellViewModel and
        // RunProgressViewModel subscribe to, after the runner has committed
        // the phase to IRunRepository.
        services.AddSingleton<CloudJobRunner>(sp =>
        {
            var messenger = sp.GetRequiredService<IMessenger>();
            return new CloudJobRunner(
                sp.GetRequiredService<IComputeGateway>(),
                sp.GetRequiredService<IStorageGateway>(),
                sp.GetRequiredService<IProjectSetupGateway>(),
                sp.GetRequiredService<IQuotaGateway>(),
                sp.GetRequiredService<IRunRepository>(),
                (jobId, phase) => messenger.Send(new RunPhaseChangedMessage(jobId, phase)));
        });
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
        // Issues #72/#73: the igv.js viewer page's state and its WebView2 host.
        services.AddSingleton<IWebViewRuntimeProbe, WebView2RuntimeProbe>();
        services.AddTransient<IgvViewerHost>();
        services.AddTransient<ViewerViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<CloudResourcesViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services;
    }
}
