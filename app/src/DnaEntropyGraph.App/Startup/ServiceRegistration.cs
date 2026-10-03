using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.Runs;
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
    /// The production entry point: <paramref name="root"/> is the one resolved data folder (issue #638: the default, or the
    /// <c>--profile</c> / <c>DEG_DATA_DIR</c> override), and every service that owns a path takes it from there.
    /// </summary>
    public static IServiceCollection AddDnaEntropyGraph(this IServiceCollection services, AppDataRoot root)
        => services.Register(root, hermetic: false);

    /// <summary>
    /// <paramref name="appDataRoot"/> lets a test redirect all app state away from the real
    /// <c>%LOCALAPPDATA%\DNAEntropyGraph\</c> to a throwaway temp directory (<c>null</c> means the real default folder).
    /// Building this graph under <c>ValidateOnBuild</c> eagerly constructs every singleton; without the redirect that has a real
    /// side effect on the machine running the test. MEASURED 2026-09-19: running the guard suite left an empty
    /// <c>%LOCALAPPDATA%\DNAEntropyGraph\</c> on this machine. A test root is also hermetic: the OAuth client is looked for only
    /// inside it, never in the repo's secrets folder.
    /// </summary>
    public static IServiceCollection AddDnaEntropyGraph(this IServiceCollection services, string? appDataRoot = null)
        => appDataRoot is null
            ? services.Register(AppDataRoot.Default(), hermetic: false)
            : services.Register(AppDataRoot.FromPath(appDataRoot), hermetic: true);

    private static IServiceCollection Register(this IServiceCollection services, AppDataRoot root, bool hermetic)
    {
        var databasePath = root.DatabaseFile;
        var settingsPath = root.SettingsFile;
        services.AddSingleton<AppDataRoot>(root);

        // App-owned, WinUI-bound services (Presentation depends on their
        // interfaces only - docs/architecture.md section 2).
        services.AddSingleton<IDispatcher, DispatcherAdapter>();
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigator>(sp => sp.GetRequiredService<NavigationService>());
        // Issue #585: the shell message bar is the toast service; one instance behind both types.
        services.AddSingleton<InAppMessageCenter>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<InAppMessageCenter>());
        services.AddSingleton<WindowHandleProvider>();
        services.AddSingleton<IFilePicker, FilePickerService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
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
            AuthDirectory = root.AuthDirectory,
            ClientLoader = new OAuthClientLoader(hermetic
                ? [Path.Combine(root.Path, OAuthClientLoader.FileName)]
                : OAuthClientLoader.DefaultCandidates(root.Path, AppContext.BaseDirectory)),
            Browser = new SystemBrowserLauncher(),

            // Only a fallback since #520 (a project the account chose wins): while every gateway is FakeGcp, an account
            // with no chosen project still reaches the not-connected gateways and fails as cloud_not_connected rather than
            // as no_project, which names a project picker the app has no page for yet. Remove with the real gateways (#609).
            ProjectIdUntilSelectionExists = "fake-project",
            Pages = new LoopbackPages(
                () => sp.GetRequiredService<IStringResourceProvider>().GetString("SignInBrowserSuccess"),
                () => sp.GetRequiredService<IStringResourceProvider>().GetString("SignInBrowserFailure")),
        });
        services.AddSingleton<GoogleAccountService>();
        services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<GoogleAccountService>());
        // No consumer yet: the real gateways (#56) take their access token from here. Registered now so that swap
        // touches only the gateways.
        services.AddSingleton<IGcpAccessTokenSource>(sp => sp.GetRequiredService<GoogleAccountService>());

        // Issue #258: every gateway the app resolves is wrapped in the one
        // resilience pipeline (retry with jitter on 429/5xx/transport, a
        // single token refresh on 401, a circuit breaker for the offline
        // banner). The real Google-backed gateways replace FakeGcp INSIDE
        // these wrappers. A test that needs fast retries registers its own
        // CloudRetryOptions after calling this method (the last one wins).
        services.AddSingleton<CloudRetryOptions>(_ => new CloudRetryOptions());
        services.AddSingleton<CloudRetryLog>();
        // Issue #530: the pipeline reports to this wrapper, which forwards to the log (the offline banner) and runs the reconciler again when the
        // connection comes back. The reconciler is resolved lazily, on the first reconnect: it needs the gateways, which need this observer.
        services.AddSingleton<ReconcileOnReconnect>(sp => new ReconcileOnReconnect(
            sp.GetRequiredService<CloudRetryLog>(),
            () => sp.GetRequiredService<JobReconciler>(),
            sp.GetRequiredService<IDiagnosticsLog>()));
        services.AddSingleton<ICloudCallObserver>(sp => sp.GetRequiredService<ReconcileOnReconnect>());
        // SWITCH POINT (#56): while every gateway is FakeGcp the refresher is FakeGcp too, because a 401 from a fake
        // must not call the real token endpoint (it would throw SIGNIN_EXPIRED for an account nothing real asked
        // about). When the first real gateway is wrapped, register GoogleAccountService here instead.
        services.AddSingleton<ICloudTokenRefresher>(sp => sp.GetRequiredService<FakeGcp>());
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
        services.AddSingleton<IProjectRepository>(sp => new ProjectRepository(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<ISettingsStore>(_ => new SettingsStore(settingsPath));

        // Issue #460: the app's own copy of every run's input, under the same app data folder as the
        // database and settings (Hard Rule 14).
        services.AddSingleton<IRunInputStore>(_ => new LocalRunInputStore(root.Path));

        // Issue #530: where the reconciler records an error it did not expect (job id and error class only), under the same app data folder.
        services.AddSingleton<IDiagnosticsLog>(_ => new FileDiagnosticsLog(root.Path));

        // Issue #63: a pasted sequence is saved under app data too, never next to anything of the user's.
        services.AddSingleton<IPastedInputStore>(_ => new LocalPastedInputStore(root.Path));

        // Issue #101: the Runs page's services. Output folders are only ever deleted from under the run's own
        // output folder, and never from the app data folder that holds the input copies (Hard Rule 14).
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IRunHistoryRemover>(sp => (RunRepository)sp.GetRequiredService<IRunRepository>());
        services.AddSingleton<ILocalRunFiles>(sp => new LocalRunFiles(
            sp.GetRequiredService<IRunInputStore>(),
            () => RunOutputFolders.DefaultParent(Services.KnownFolders.Downloads),
            [root.Path]));
        services.AddSingleton<IJobObjectDeleter, UnconnectedJobObjectDeleter>();

        // Issue #102: the Results page reads a run's own output folder and opens its files through Windows.
        services.AddSingleton<IRunOutputReader, RunOutputReader>();

        // Issue #586: Open in IGV (its batch port, else launch) and Open in Geneious (launch).
        services.AddSingleton<DnaEntropyGraph.Core.Viewers.IIgvBatchClient, DnaEntropyGraph.Core.Viewers.TcpIgvBatchClient>();
        services.AddSingleton<DnaEntropyGraph.Core.Viewers.IViewerLocator, DnaEntropyGraph.Core.Viewers.ViewerLocator>();
        services.AddSingleton<DnaEntropyGraph.Core.Viewers.IViewerProcessLauncher, Services.ViewerProcessLauncher>();
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IExternalViewerOpener, DnaEntropyGraph.Presentation.Services.ExternalViewerOpener>();
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IShellLauncher, Services.ShellLauncher>();
        services.AddSingleton<IRunCloudResults>(sp => new RunCloudResults(
            sp.GetRequiredService<IStorageGateway>(),
            sp.GetRequiredService<IJobObjectDeleter>(),
            sp.GetRequiredService<IRunRepository>(),
            () => RunOutputFolders.DefaultParent(Services.KnownFolders.Downloads),
            sp.GetRequiredService<TimeProvider>()));

        // Issue #106: the support zip. The source only ever lists settings.json, logs and runs under the app data
        // folder (never auth, inputs or the database); the machine facts are read when the button is pressed.
        services.AddSingleton<IFolderLauncher, FolderLauncher>();
        services.AddSingleton<IDiagnosticsExporter>(sp => new DiagnosticsExporter(
            new FolderDiagnosticsSource(root.Path),
            sp.GetRequiredService<IRunRepository>(),
            () => DiagnosticsInfoProvider.Current(sp.GetRequiredService<IGcpAccount>(), sp.GetRequiredService<IStringResourceProvider>())));

        // Issue #458: the worker image comes from the list pinned by digest that ships with the app.
        services.AddSingleton<PinnedWorkerImageList>(_ => PinnedWorkerImageProvider.LoadShippedList());
        services.AddSingleton<IWorkerImageProvider, PinnedWorkerImageProvider>();
        // Issue #98: the price list ships beside the app (Assets\pricing.json in the output folder) and the New run page's
        // estimate reads it through this one service. A missing file is a named message on the page, not a crash.
        services.AddSingleton<DnaEntropyGraph.Core.Cost.IPricingSource>(_ => new DnaEntropyGraph.Core.Cost.FilePricingSource(
            Path.Combine(AppContext.BaseDirectory, "Assets", "pricing.json")));
        services.AddSingleton<DnaEntropyGraph.Core.Cost.ICostEstimateService, DnaEntropyGraph.Core.Cost.CostEstimateService>();

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
        // Issue #59: on launch, runs a killed app left non-terminal are reattached through the runner above.
        // AppStartup.BeginAsync (called once from App.OnLaunched) is what invokes it.
        // The one registry of "the task driving this job": the engine's runs and the reconciler's reattached runs both go through it, so Cancel finds either.
        services.AddSingleton<ActiveRuns>();
        services.AddSingleton<JobReconciler>(sp =>
        {
            var messenger = sp.GetRequiredService<IMessenger>();
            return new JobReconciler(
                sp.GetRequiredService<CloudJobRunner>(),
                sp.GetRequiredService<IComputeGateway>(),
                sp.GetRequiredService<IStorageGateway>(),
                sp.GetRequiredService<IRunRepository>(),
                sp.GetRequiredService<IRunInputStore>(),
                sp.GetRequiredService<IWorkerImageProvider>(),
                sp.GetRequiredService<ActiveRuns>(),
                sp.GetRequiredService<ISettingsStore>(),
                (jobId, phase) => messenger.Send(new RunPhaseChangedMessage(jobId, phase)),
                Services.KnownFolders.Downloads,
                log: sp.GetRequiredService<IDiagnosticsLog>());
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
