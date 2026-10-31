using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cloud.Audit;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Net;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Setup;
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
    public static IServiceCollection AddDnaEntropyGraph(this IServiceCollection services, AppDataRoot root, CloudBackend cloud = CloudBackend.Google)
        => services.Register(root, hermetic: false, cloud);

    /// <summary>
    /// <paramref name="appDataRoot"/> lets a test redirect all app state away from the real
    /// <c>%LOCALAPPDATA%\DNAEntropyGraph\</c> to a throwaway temp directory (<c>null</c> means the real default folder).
    /// Building this graph under <c>ValidateOnBuild</c> eagerly constructs every singleton; without the redirect that has a real
    /// side effect on the machine running the test. MEASURED 2026-09-19: running the guard suite left an empty
    /// <c>%LOCALAPPDATA%\DNAEntropyGraph\</c> on this machine. A test root is also hermetic: the OAuth client is looked for only
    /// inside it, never in the repo's secrets folder.
    /// </summary>
    public static IServiceCollection AddDnaEntropyGraph(this IServiceCollection services, string? appDataRoot = null, CloudBackend cloud = CloudBackend.Google)
        => appDataRoot is null
            ? services.Register(AppDataRoot.Default(), hermetic: false, cloud)
            : services.Register(AppDataRoot.FromPath(appDataRoot), hermetic: true, cloud);

    private static IServiceCollection Register(this IServiceCollection services, AppDataRoot root, bool hermetic, CloudBackend cloud)
    {
        var databasePath = root.DatabaseFile;
        var settingsPath = root.SettingsFile;
        services.AddSingleton<AppDataRoot>(root);

        // App-owned, WinUI-bound services (Presentation depends on their
        // interfaces only - docs/architecture.md section 2).
        services.AddSingleton<IDispatcher, DispatcherAdapter>();
        // Issue #825: one messenger per container, never the process-wide static, so two containers (tests run in parallel) never hear each other.
        services.AddSingleton<IMessenger>(_ => new WeakReferenceMessenger());
        services.AddSingleton<NavigationService>();
        // Issue #244: every navigation also records the page (and run) for session restore; the shell reads it back at startup.
        services.AddSingleton<SessionStore>();
        services.AddSingleton<SessionRestorer>();
        services.AddSingleton<INavigator>(sp => new RecordingNavigator(sp.GetRequiredService<NavigationService>(), sp.GetRequiredService<SessionStore>()));
        // Issue #585: the shell message bar is the toast service; one instance behind both types.
        services.AddSingleton<InAppMessageCenter>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<InAppMessageCenter>());
        // Issue #701: the high contrast state behind the window theme, and the listener that re-applies it when it changes at runtime.
        services.AddSingleton<IHighContrastSource, AccessibilityHighContrastSource>();
        services.AddSingleton<HighContrastThemeReapplier>();
        services.AddSingleton<WindowThemeApplier>();
        services.AddSingleton<IThemeApplier>(sp => sp.GetRequiredService<WindowThemeApplier>());
        services.AddSingleton<WindowHandleProvider>();

        // Issue #107: the window as Presentation sees it, the Windows notification, and what a click on one (or a second launch) does.
        services.AddSingleton<AppWindowService>();
        services.AddSingleton<IAppWindow>(sp => sp.GetRequiredService<AppWindowService>());
        services.AddSingleton<INotificationSink, WindowsNotificationSink>();
        services.AddSingleton<IDesktopNotifier, AppNotifier>();
        services.AddSingleton<ShellReadiness>();
        services.AddSingleton<NewRunIntake>();
        services.AddSingleton<ActivationRouter>();

        // Issue #108: "Open with" entries, written under the current user only; the Settings switch is the only caller.
        services.AddSingleton<IClassesRegistry, CurrentUserClasses>();
        services.AddSingleton(sp => new FileAssociationService(
            sp.GetRequiredService<IClassesRegistry>(),
            () => Environment.ProcessPath,
            () => sp.GetRequiredService<AppDataRoot>().IsOverride,
            () => sp.GetRequiredService<AppDataRoot>().IsPortable));
        services.AddTransient<FileAssociationSettingsViewModel>();
        services.AddSingleton<IFilePicker, FilePickerService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        // Issue #675: the three-button question (Stop / Delete / Keep waiting) is the same window's ContentDialog.
        services.AddSingleton<IChoiceDialogService>(sp => sp.GetRequiredService<DialogService>());
        // Issue #675: the runner's slow-run notice and stall question, answered by the UI (replaces the log-only defaults below).
        services.AddSingleton<RunStallCoordinator>(sp => new RunStallCoordinator(
            sp.GetRequiredService<IChoiceDialogService>(),
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<IStringResourceProvider>(),
            sp.GetRequiredService<IDiagnosticsLog>()));
        services.AddSingleton<IStringResourceProvider, ReswStringResourceProvider>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<ILogTailReader, FileLogTailReader>();

        // Issue #239: the one network proxy policy (follow Windows, a typed proxy, or none) and the encrypted file for a proxy sign-in. Every real HTTP client (Google sign-in,
        // the Google gateways, the Velopack update check) holds a PolicyWebProxy over this one object, so a change in Settings reaches the next request.
        services.AddSingleton<IProxyCredentialStore>(_ => new ProxyCredentialFile(Path.Combine(root.AuthDirectory, "proxy.cred"), new DpapiSecretProtector()));
        services.AddSingleton<ProxyPolicy>(sp => new ProxyPolicy(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IProxyCredentialStore>()));
        services.AddSingleton<IProxyPolicy>(sp => sp.GetRequiredService<ProxyPolicy>());
        services.AddTransient<ProxySettingsViewModel>();

        // Cloud (Hard Rule 7: the only project allowed to reference Google.*). Issue #48: the account is the real Google sign-in
        // (loopback + PKCE, DPAPI token files keyed by sub), in both backends. A build with no
        // oauth_client.local.json cannot sign in and says so by code (OAUTH_CLIENT_MISSING), never by crashing.
        // Every path is resolved lazily on first use, so building this graph (Guards.Tests) touches no disk.
        services.AddSingleton<GoogleAccountOptions>(sp => new GoogleAccountOptions
        {
            AuthDirectory = root.AuthDirectory,
            ClientLoader = new OAuthClientLoader(hermetic
                ? [Path.Combine(root.Path, OAuthClientLoader.FileName)]
                : OAuthClientLoader.DefaultCandidates(root.Path, AppContext.BaseDirectory)),
            Browser = new SystemBrowserLauncher(),
            Proxy = sp.GetRequiredService<IProxyPolicy>(),

            // Only the fake backend answers a project nobody chose (a project the account chose wins, #520). The Google backend
            // answers null: no chosen project means a run fails with no_project, which names the one way out (#609).
            ProjectIdUntilSelectionExists = cloud == CloudBackend.Fake ? "fake-project" : null,
            Pages = new LoopbackPages(
                () => sp.GetRequiredService<IStringResourceProvider>().GetString("SignInBrowserSuccess"),
                () => sp.GetRequiredService<IStringResourceProvider>().GetString("SignInBrowserFailure")),
        });
        if (cloud == CloudBackend.Developer)
        {
            // Developer mode (issue #69): no real account service exists in this container at all, so nothing can reach Google through it.
            services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<FakeGcp>());
            services.AddSingleton<IGcpAccessTokenSource, FakeCloudMode.FakeTokenSource>();
            services.AddSingleton<DnaEntropyGraph.Presentation.Services.SimulatedCloudMode>();
        }
        else
        {
            services.AddSingleton<GoogleAccountService>();
            services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<GoogleAccountService>());
            // The real gateways take their access token from here.
            services.AddSingleton<IGcpAccessTokenSource>(sp => sp.GetRequiredService<GoogleAccountService>());
        }

        // Issue #258: every cloud call goes through the one resilience pipeline (retry with jitter on 429/5xx/transport, a
        // single token refresh on 401, a circuit breaker for the offline banner). The Google gateways run it themselves, per
        // HTTP request; the fake backend gets it through the Resilient* wrappers. A test that needs fast retries registers
        // its own CloudRetryOptions after calling this method (the last one wins).
        services.AddSingleton<CloudRetryOptions>(_ => new CloudRetryOptions());
        services.AddSingleton<CloudRetryLog>();
        // Issue #530: the pipeline reports to this wrapper, which forwards to the log (the offline banner) and runs the reconciler again when the
        // connection comes back. The reconciler is resolved lazily, on the first reconnect: it needs the gateways, which need this observer.
        services.AddSingleton<ReconcileOnReconnect>(sp => new ReconcileOnReconnect(
            sp.GetRequiredService<CloudRetryLog>(),
            () => sp.GetRequiredService<JobReconciler>(),
            sp.GetRequiredService<IDiagnosticsLog>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICloudCallObserver>(sp => sp.GetRequiredService<ReconcileOnReconnect>());
        services.AddSingleton<CloudCallPipeline>(sp => new CloudCallPipeline(
            sp.GetRequiredService<CloudRetryOptions>(),
            sp.GetRequiredService<ICloudTokenRefresher>(),
            sp.GetRequiredService<ICloudCallObserver>()));

        // SWITCH POINT (#609, docs/cloud_design.md section 16): the one place production chooses Google or the fake.
        if (cloud != CloudBackend.Google)
        {
            AddFakeCloud(services, connected: cloud == CloudBackend.Developer);
        }
        else
        {
            AddGoogleCloud(services);
        }

        // Issue #245: the audit of every cloud mutation wraps whichever gateways the switch above registered.
        CloudAuditRegistration.Add(services, root);

        // Persistence (issue #67: real SQLite + settings.json under
        // %LOCALAPPDATA%\DNAEntropyGraph\, docs/architecture.md section 6).
        // SqliteDatabase and SettingsStore both take a file path, so each
        // gets a factory registration rather than the type-to-type shorthand
        // the rest of this method uses.
        services.AddSingleton(_ => new SqliteDatabase(databasePath, portable: root.IsPortable));
        services.AddSingleton<RunRepository>(sp => new RunRepository(sp.GetRequiredService<SqliteDatabase>(), root.PathMapper, sp.GetRequiredService<IDiagnosticsLog>()));
        // Issue #109: every writer of a run row (runner, reconciler, cleanup) goes through this decorator, which writes the run's
        // CostLedger row the first time the row reaches an ended phase. The same SpendLedger is the cap gate the run start and the
        // New run page ask, so the number that is written is the number that is checked.
        services.AddSingleton<ICostLedgerRepository>(sp => new CostLedgerRepository(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<SpendLedger>(sp => new SpendLedger(
            sp.GetRequiredService<ICostLedgerRepository>(),
            sp.GetRequiredService<DnaEntropyGraph.Core.Cost.IPricingSource>(),
            sp.GetRequiredService<ISettingsStore>(),
            TimeProvider.System,
            runs: sp.GetRequiredService<RunRepository>()));
        services.AddSingleton<DnaEntropyGraph.Core.Cost.ISpendCapGate>(sp => sp.GetRequiredService<SpendLedger>());
        services.AddSingleton<IRunRepository>(sp => new LedgerRecordingRunRepository(sp.GetRequiredService<RunRepository>(), sp.GetRequiredService<SpendLedger>()));
        services.AddSingleton<IProjectRepository>(sp => new ProjectRepository(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<SettingsStore>(_ => new SettingsStore(settingsPath));
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<SettingsStore>());
        services.AddSingleton<IResettableSettings>(sp => sp.GetRequiredService<SettingsStore>());
        services.AddSingleton<ICloudModeInfo>(_ => new DnaEntropyGraph.App.Services.FakeCloudModeInfo(cloud));
        services.AddSingleton<OutputSelectionStore>();
        services.AddSingleton<DnaEntropyGraph.Core.Presets.PresetStore>();

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
        services.AddSingleton<IRunHistoryRemover>(sp => sp.GetRequiredService<RunRepository>());
        services.AddSingleton<IRunAnnotations>(sp => sp.GetRequiredService<RunRepository>());
        services.AddSingleton<ILocalRunFiles>(sp => new LocalRunFiles(
            sp.GetRequiredService<IRunInputStore>(),
            () => RunOutputFolders.DefaultParent(Services.KnownFolders.Downloads, root.PortableOutputsDirectory),
            [root.Path]));
        // Issues #262 and #534: the real deleter, scoped to jobs/<job id>/ in a deg- bucket and read back afterwards.
        services.AddSingleton<IJobObjectDeleter>(sp => new JobObjectDeleter(
            sp.GetRequiredService<IStorageGateway>(),
            () =>
            {
                // Read at each delete; an unusable id file answers null and the delete is refused rather than guessing ownership.
                try
                {
                    return InstallationId.GetOrCreate(sp.GetRequiredService<ISettingsStore>());
                }
                catch (InstallationIdUnusableException)
                {
                    return null;
                }
            }));

        // Issue #102: the Results page reads a run's own output folder and opens its files through Windows.
        services.AddSingleton<IRunOutputReader, RunOutputReader>();

        // Issue #586: Open in IGV (its batch port, else launch) and Open in Geneious (launch).
        services.AddSingleton<DnaEntropyGraph.Core.Viewers.IIgvBatchClient, DnaEntropyGraph.Core.Viewers.TcpIgvBatchClient>();
        services.AddSingleton<DnaEntropyGraph.Core.Viewers.IViewerLocator, DnaEntropyGraph.Core.Viewers.ViewerLocator>();
        services.AddSingleton<DnaEntropyGraph.Core.Viewers.IViewerProcessLauncher, Services.ViewerProcessLauncher>();
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IExternalViewerOpener, DnaEntropyGraph.Presentation.Services.ExternalViewerOpener>();
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IShellLauncher, Services.ShellLauncher>();
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IClipboardTextReader, Services.ClipboardTextReader>();
        services.AddSingleton<DnaEntropyGraph.Core.Tracks.IEntropyOverviewSource, DnaEntropyGraph.Core.Tracks.EntropyOverviewSource>();
        services.AddSingleton<DnaEntropyGraph.Core.Tracks.IGeneTableSource, DnaEntropyGraph.Core.Tracks.GeneTableSource>();
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IOverviewImageExporter, Services.OverviewPngExporter>();
        services.AddSingleton(new DnaEntropyGraph.Presentation.Services.ResultsExportRoot(() => RunOutputFolders.DefaultParent(Services.KnownFolders.Downloads, root.PortableOutputsDirectory), appOwned: root.IsPortable));
        // Issue #111: Find runs from other computers (read-only listing of the project's results bucket).
        services.AddSingleton<ImportedRunDismissals>(sp => new ImportedRunDismissals(sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<IRunImporter>(sp => new RunImporter(sp.GetRequiredService<IStorageGateway>(), sp.GetRequiredService<IRunRepository>(), sp.GetRequiredService<ImportedRunDismissals>()));
        services.AddSingleton<IRunCloudResults>(sp => new RunCloudResults(
            sp.GetRequiredService<IStorageGateway>(),
            sp.GetRequiredService<IJobObjectDeleter>(),
            sp.GetRequiredService<IRunRepository>(),
            () => RunOutputFolders.DefaultParent(Services.KnownFolders.Downloads, root.PortableOutputsDirectory),
            sp.GetRequiredService<TimeProvider>()));

        // Issue #106: the support zip. The source only ever lists settings.json, logs and runs under the app data
        // folder (never auth, inputs or the database); the machine facts are read when the button is pressed.
        services.AddSingleton<IFolderLauncher, FolderLauncher>();
        services.AddSingleton<IDiagnosticsExporter>(sp => new DiagnosticsExporter(
            new FolderDiagnosticsSource(root.Path),
            sp.GetRequiredService<IRunRepository>(),
            () => DiagnosticsInfoProvider.Current(sp.GetRequiredService<IGcpAccount>(), sp.GetRequiredService<IStringResourceProvider>(), sp.GetRequiredService<IProxyPolicy>())));

        // Issue #458: the worker image comes from the list pinned by digest that ships with the app.
        services.AddSingleton<PinnedWorkerImageList>(_ => PinnedWorkerImageProvider.LoadShippedList());
        services.AddSingleton<IWorkerImageProvider, PinnedWorkerImageProvider>();
        // Issue #98: the price list ships beside the app (Assets\pricing.json in the output folder) and the New run page's
        // estimate reads it through this one service. A missing file is a named message on the page, not a crash.
        // Issue #214: the price catalog (read at most once a day, cached under the app data folder) sits in front of that file, which stays the
        // fallback when the catalog cannot be read; IPricingStatus says which one the numbers came from and as of when.
        services.AddSingleton<DnaEntropyGraph.Core.Cost.FilePricingSource>(_ => new DnaEntropyGraph.Core.Cost.FilePricingSource(
            Path.Combine(AppContext.BaseDirectory, "Assets", "pricing.json")));
        services.AddSingleton<DnaEntropyGraph.Core.Cost.CatalogPricingSource>(sp => new DnaEntropyGraph.Core.Cost.CatalogPricingSource(
            sp.GetRequiredService<DnaEntropyGraph.Core.Cost.FilePricingSource>(),
            sp.GetRequiredService<IPricingCatalogGateway>(),
            new DnaEntropyGraph.Core.Cost.FilePricingCacheStore(Path.Combine(root.Path, "pricing-cache.json")),
            TimeProvider.System));
        services.AddSingleton<DnaEntropyGraph.Core.Cost.IPricingSource>(sp => sp.GetRequiredService<DnaEntropyGraph.Core.Cost.CatalogPricingSource>());
        services.AddSingleton<DnaEntropyGraph.Core.Cost.IPricingStatus>(sp => sp.GetRequiredService<DnaEntropyGraph.Core.Cost.CatalogPricingSource>());
        services.AddSingleton<DnaEntropyGraph.Core.Cost.ICostEstimateService, DnaEntropyGraph.Core.Cost.CostEstimateService>();

        // LocalEngine (issues #120, #172, #173, #174): the This PC path. Everything that starts a program goes through the one
        // IProcessLauncher (Hard Rule 6), so a test or a dev run can replace it.
        // One kill-on-close Job Object for the whole app (issue #734): every child it starts dies with it, however the app ends. The container disposes it at a clean exit.
        services.AddSingleton<WindowsKillOnCloseJob>();
        services.AddSingleton<IProcessLauncher>(sp => new SystemProcessLauncher(sp.GetRequiredService<WindowsKillOnCloseJob>()));
        services.AddSingleton<IGpuDetector>(sp => new NvidiaSmiGpuDetector(sp.GetRequiredService<IProcessLauncher>(), NvidiaSmiGpuDetector.WindowsCandidates()));
        services.AddSingleton<IFreeDiskSpace, DriveFreeDiskSpace>();
        services.AddSingleton<LocalEngineLayout>(sp => LocalEngineSetup.Layout(sp.GetRequiredService<AppDataRoot>()));
        services.AddSingleton<LocalEngineManager>(sp => new LocalEngineManager(
            sp.GetRequiredService<IGpuDetector>(),
            sp.GetRequiredService<IFreeDiskSpace>(),
            sp.GetRequiredService<IProcessLauncher>(),
            sp.GetRequiredService<LocalEngineLayout>(),
            JobEngine.AppVersion()));
        services.AddSingleton<ILocalReadinessSource>(sp => sp.GetRequiredService<LocalEngineManager>());
        services.AddSingleton<IRunTargetResolver, RunTargetResolver>();
        services.AddSingleton<ILocalEngineInstaller>(sp => LocalEngineSetup.Installer(
            sp.GetRequiredService<IProcessLauncher>(),
            sp.GetRequiredService<LocalEngineLayout>(),
            sp.GetRequiredService<AppDataRoot>(),
            sp.GetRequiredService<LocalEngineManager>()));
        services.AddSingleton<LocalJobRunner>(sp =>
        {
            var messenger = sp.GetRequiredService<IMessenger>();
            return new LocalJobRunner(
                sp.GetRequiredService<IProcessLauncher>(),
                sp.GetRequiredService<IRunRepository>(),
                sp.GetRequiredService<LocalEngineLayout>(),
                sp.GetRequiredService<AppDataRoot>().RunsDirectory,
                (jobId, phase) => messenger.Send(new RunPhaseChangedMessage(jobId, phase)))
            {
                DefaultOutputParent = () => RunOutputFolders.DefaultParent(Services.KnownFolders.Downloads, root.PortableOutputsDirectory),
            };
        });
        services.AddSingleton<LocalRunDriver>();

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
            var diagnostics = sp.GetRequiredService<IDiagnosticsLog>();
            return new CloudJobRunner(
                sp.GetRequiredService<IComputeGateway>(),
                sp.GetRequiredService<IStorageGateway>(),
                sp.GetRequiredService<IProjectSetupGateway>(),
                sp.GetRequiredService<IQuotaGateway>(),
                sp.GetRequiredService<IWorkerIdentityGateway>(),
                sp.GetRequiredService<IRunRepository>(),
                (jobId, phase) => messenger.Send(new RunPhaseChangedMessage(jobId, phase)))
            {
                // Issue #938: how the clean-up of a create that landed after a cancel ended goes to the diagnostics log (the run id, the state and an error class, never a name, a file or an email);
                // what the user must act on is also on the Cancelled row as an error code (VmCanceller.CodeFor). The reap window keeps its default (15 minutes).
                OnLateCreate = outcome => diagnostics.Warning("late-create", outcome.JobId, outcome.LogClass),
                // Issue #970: what became of the labels of a VM a failed or cancelled run had borrowed by reuse (the run id, the state and an error class only).
                OnDonorRestore = outcome => diagnostics.Warning("donor-restore", outcome.JobId, outcome.LogClass),
                // Issue #675: the stall question is a Stop / Delete / Keep waiting dialog and the slow-run notice is a line on the run page (RunStallCoordinator; the notice is also
                // logged by id and kind, never a sequence, a file name or an email). A dialog that cannot be shown answers Keep waiting; StallDecisions.EndAsRunChose stays the
                // default for a runner built without a UI (tests, the reconciler's own runner).
                OnStall = sp.GetRequiredService<RunStallCoordinator>().AskAsync,
                OnNotice = sp.GetRequiredService<RunStallCoordinator>().OnNotice,
            };
        });
        // Issue #59: on launch, runs a killed app left non-terminal are reattached through the runner above.
        // AppStartup.BeginAsync (called once from App.OnLaunched) is what invokes it.
        // The one registry of "the task driving this job": the engine's runs and the reconciler's reattached runs both go through it, so Cancel finds either.
        services.AddSingleton<ActiveRuns>();
        // Issue #99: the wizard's GPU step asks Google for the L4 and project-wide GPU quota through this (the app lane binds it).
        services.AddSingleton<GpuQuotaRequester>(sp => new GpuQuotaRequester(sp.GetRequiredService<IQuotaGateway>(), sp.GetRequiredService<IGcpAccount>()));
        // Issue #105: what this installation has in the project, and "delete everything": for the Cloud page (the app lane binds it).
        services.AddSingleton<InstallationCleanup>(sp =>
        {
            // Run rows and the driver registry are what end a run whose VM the cleanup deletes (Hard Rule 11); without them it would leave the row Running.
            var messenger = sp.GetRequiredService<IMessenger>();
            return new InstallationCleanup(
                sp.GetRequiredService<IComputeGateway>(),
                sp.GetRequiredService<IStorageGateway>(),
                sp.GetRequiredService<IRunRepository>(),
                sp.GetRequiredService<ActiveRuns>(),
                (jobId, phase) => messenger.Send(new RunPhaseChangedMessage(jobId, phase)));
        });
        // Issue #113: the nothing-left-running scan (launch, every 5 minutes, and the exit modal); started by AppStartup, asked on close by AppWindowService.
        services.AddSingleton<LeftRunningScanner>(sp => new LeftRunningScanner(
            sp.GetRequiredService<IComputeGateway>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IRunRepository>(),
            sp.GetRequiredService<DnaEntropyGraph.Core.Cost.IPricingSource>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<LeftRunningGuard>(sp => new LeftRunningGuard(
            sp.GetRequiredService<LeftRunningScanner>(),
            sp.GetRequiredService<InstallationCleanup>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IChoiceDialogService>(),
            sp.GetRequiredService<IToastService>(),
            sp.GetRequiredService<IStringResourceProvider>(),
            sp.GetRequiredService<IDiagnosticsLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IGcpAccount>()));
        services.AddSingleton<RunAccountBackfill>(sp => new RunAccountBackfill(
            sp.GetRequiredService<IRunRepository>(), sp.GetRequiredService<IGcpAccount>(), sp.GetRequiredService<ISettingsStore>()));
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
                root.DefaultOutputSource(Services.KnownFolders.Downloads),
                log: sp.GetRequiredService<IDiagnosticsLog>(),
                additionalSweepScopes: () => sp.GetRequiredService<IGcpAccount>().ActiveSnapshot() is { ProjectId.Length: > 0 } active ? [new CloudScope(active.ProjectId, active.AccountSub)] : [],
                onSignInNeeded: sub => messenger.Send(new AccountSignInNeededMessage(sub)));
        });
        // Issue #684 / Hard Rule 2: the one NCBI request that says an accession's record exists and fits, before any cloud resource. Only the real
        // build reaches NCBI; the fake backends (tests, developer mode) answer from a script so nothing live is ever asked.
        services.AddSingleton<IAccessionLookup>(sp =>
        {
            if (cloud != CloudBackend.Google)
            {
                return new ScriptedAccessionLookup();
            }

            // Issue #239: through the one proxy policy, like every other outbound client (a bare HttpClient would ignore a typed proxy).
            return new NcbiAccessionLookup(DnaEntropyGraph.Cloud.Net.ProxiedHttpClient.Create(sp.GetRequiredService<IProxyPolicy>(), 1024 * 1024, "dna-entropy-graph"));
        });
        services.AddSingleton<JobEngine>();
        services.AddSingleton<IJobEngine>(sp => sp.GetRequiredService<JobEngine>());
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IRunVmActions>(sp => sp.GetRequiredService<JobEngine>());

        // Every ViewModel (docs/superpowers/specs Appendix A section 1's
        // key-classes table). Transient: a fresh instance per page
        // navigation, per WinUI convention.
        services.AddTransient<ShellViewModel>();
        // Issue #99: the wizard's steps 3 to 7 are plain calls over the cloud interfaces (real Google or FakeGcp, whichever the container was
        // built over), so the same service backs the wizard page and, later, the Settings health report.
        services.AddSingleton<BillingSetup>();
        services.AddSingleton<ISetupCheckService, SetupCheckService>();
        services.AddTransient<SetupStepsViewModel>();
        services.AddTransient<WizardViewModel>();
        services.AddTransient<PresetPickerViewModel>();
        // Issue #87: the New run page's linked model and graphics card pickers, over the one shipped catalog.
        services.AddSingleton<GpuCatalog>(_ => GpuCatalog.Shipped);
        services.AddTransient<GpuPlanViewModel>();
        // Issue #527: one instance for the app, so the files on the New run page survive a trip to another page and back; it empties itself when a run starts.
        services.AddSingleton<NewRunViewModel>();
        services.AddTransient<RunProgressViewModel>();
        services.AddTransient<ResultsViewModel>();
        // Issues #72/#73: the igv.js viewer page's state and its WebView2 host.
        services.AddSingleton<IWebViewRuntimeProbe, WebView2RuntimeProbe>();
        services.AddTransient<IgvViewerHost>();
        services.AddTransient<ViewerViewModel>();
        // Issue #497: the .fai of a long FASTA is built into the app's own folder, never beside the user's file.
        services.AddSingleton<DnaEntropyGraph.Core.Viewers.IFastaIndexStore>(sp =>
            new DnaEntropyGraph.Core.Viewers.FastaIndexStore(sp.GetRequiredService<AppDataRoot>().FastaIndexDirectory));
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<CloudResourcesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<RunDefaultsSettingsViewModel>();
        services.AddTransient<FilesSettingsViewModel>();
        services.AddTransient<CloudActivityLogSettingsViewModel>();
        services.AddTransient<NotificationsSettingsViewModel>();
        services.AddTransient<AdvancedSettingsViewModel>();
        services.AddTransient<SpendCapSettingsViewModel>();
        // One picker for the whole app (Settings and New run), so a change made in Settings is what the next run uses without reopening any page.
        services.AddSingleton<RunTargetPickerViewModel>();
        services.AddTransient<ThisPcSettingsViewModel>();
        services.AddTransient<CloudResultsSettingsViewModel>();
        services.AddSingleton<NotificationSettings>();
        // Issue #143: one tour state shared by the window's tips and the Settings replay button.
        services.AddSingleton<TourSettings>();
        services.AddSingleton<TourViewModel>();
        services.AddSingleton<SignInNeededViewModel>();
        // Issue #179: one update state for the shell's bar and the Settings About group; the source is the only thing that touches Velopack.
        services.AddSingleton<IAppUpdateSource, Services.VelopackUpdateSource>();
        services.AddSingleton<IActiveRunProbe, Services.ActiveRunProbe>();
        services.AddSingleton<UpdateSettings>();
        // Issue #263: one read of the running exe's version, sha and signature for the title-bar banner and About.
        services.AddSingleton(_ => Services.BuildIdentityFactory.Create());
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<RunCompletionActions>();
        services.AddSingleton<DnaEntropyGraph.Presentation.Services.IFileProbe, DnaEntropyGraph.Presentation.Services.DiskFileProbe>();
        services.AddTransient<ExternalViewersSettingsViewModel>();
        // Issues #148 and #222: the Settings "Budget alert" group. The service is the policy (account, API, installation id); the gateway is Google or the fake.
        services.AddSingleton<IBudgetService, BudgetService>();
        services.AddTransient<BudgetSettingsViewModel>();
        // Issue #242: export/import of settings and presets as versioned JSON, and the Settings group that drives it.
        services.AddSingleton<DnaEntropyGraph.Core.SettingsPortability.SettingsTransfer>();
        services.AddTransient<SettingsTransferViewModel>();
        // Issue #243: one look for the earlier command-line version's runs, shared by the first-launch offer and the Settings button.
        services.AddSingleton(new DnaEntropyGraph.Core.PrototypeImport.PrototypeSearchPaths(
            DnaEntropyGraph.Core.PrototypeImport.PrototypeLocations.ConfigPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            DnaEntropyGraph.Core.PrototypeImport.PrototypeLocations.Downloads(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        services.AddSingleton(sp => new DnaEntropyGraph.Core.PrototypeImport.PrototypeImporter(sp.GetRequiredService<IRunRepository>(), sp.GetRequiredService<ImportedRunDismissals>()));
        services.AddSingleton<PrototypeImportViewModel>();

        if (cloud == CloudBackend.Developer)
        {
            // Developer mode (issue #69), registered last so it wins over the shipped image list: the account is the fake too (registered above)
            // (Sign in succeeds with no browser, its project is the fake project) and the worker image is a fixed fake reference.
            services.AddSingleton<IWorkerImageProvider, FakeCloudMode.FakeWorkerImageProvider>();
        }

        return services;
    }

    /// <summary>
    /// The real build: every cloud interface is the Google-backed gateway from <see cref="GoogleCloudGateways.Create"/>, and
    /// <see cref="FakeGcp"/> is not registered at all. Each gateway is registered as built: the Compute, Storage, worker-identity,
    /// project-catalog and service-enablement gateways run every HTTP request through the pipeline themselves (their class docs say
    /// they are never wrapped: a second layer would replay a whole create or enable), and the billing gateway is already
    /// wrapped by <see cref="GoogleCloudGateways"/>. The project setup gateway only composes those three.
    /// </summary>
    private static void AddGoogleCloud(IServiceCollection services)
    {
        services.AddSingleton<ICloudTokenRefresher>(sp => sp.GetRequiredService<GoogleAccountService>());
        services.AddSingleton<GoogleCloudOptions>(sp => new GoogleCloudOptions
        {
            // Read at each use, never once at startup (#609 cold review): an unusable id file at the first run left the id null for the whole
            // process even after the user repaired it. An unusable file answers null for now (the storage gateway then refuses to create a
            // bucket by name instead of crashing; JobEngine reports installation_id_unusable before a run starts) and is read again next time.
            InstallationIdSource = () =>
            {
                try
                {
                    return InstallationId.GetOrCreate(sp.GetRequiredService<ISettingsStore>());
                }
                catch (InstallationIdUnusableException)
                {
                    return null;
                }
            },
            AppVersion = JobEngine.AppVersion(),
            Proxy = sp.GetRequiredService<IProxyPolicy>(),
            // Issue #114: the saved "Cloud results retention", read at each use so a change patches the bucket rule on the next run.
            ResultsRetentionDaysSource = () => ResultsRetentionSetting.Days(sp.GetRequiredService<ISettingsStore>()),
        });
        services.AddSingleton<GoogleCloudGatewaySet>(sp => GoogleCloudGateways.Create(
            sp.GetRequiredService<IGcpAccessTokenSource>(),
            sp.GetRequiredService<CloudCallPipeline>(),
            sp.GetRequiredService<GoogleCloudOptions>(),
            () => sp.GetRequiredService<IGcpAccount>().SelectedProjectId));
        services.AddSingleton<IComputeGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().Compute);
        services.AddSingleton<IStorageGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().Storage);
        services.AddSingleton<IProjectSetupGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().ProjectSetup);
        services.AddSingleton<IWorkerIdentityGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().WorkerIdentity);
        services.AddSingleton<IProjectCatalogGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().ProjectCatalog);
        services.AddSingleton<IBillingGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().Billing);
        services.AddSingleton<IServiceEnablementGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().Services);
        services.AddSingleton<IQuotaGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().Quota);
        services.AddSingleton<IBudgetGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().Budget);
        services.AddSingleton<IPricingCatalogGateway>(sp => sp.GetRequiredService<GoogleCloudGatewaySet>().PricingCatalog);

    }

    /// <summary>
    /// The simulated cloud, for tests and (issue #69) developer mode: <see cref="FakeGcp"/> behind the same resilience wrappers
    /// the fake-backed tests have always used. Not connected: a run fails at preflight with cloud_not_connected.
    /// </summary>
    private static void AddFakeCloud(IServiceCollection services, bool connected)
    {
        services.AddSingleton<FakeGcp>(_ => connected ? new FakeGcp() : new FakeGcp().WithCloudNotConnected());
        // A 401 from a fake must not call the real token endpoint (it would throw SIGNIN_EXPIRED for an account nothing real asked about).
        services.AddSingleton<ICloudTokenRefresher>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IComputeGateway>(sp => new ResilientComputeGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));
        services.AddSingleton<IStorageGateway>(sp => new ResilientStorageGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));
        services.AddSingleton<IProjectSetupGateway>(sp => new ResilientProjectSetupGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));
        services.AddSingleton<IQuotaGateway>(sp => new ResilientQuotaGateway(sp.GetRequiredService<FakeGcp>(), sp.GetRequiredService<CloudCallPipeline>()));
        // Issue #606: never wrapped in a Resilient* decorator (the real gateway runs its own pipeline per HTTP call).
        services.AddSingleton<IWorkerIdentityGateway>(sp => sp.GetRequiredService<FakeGcp>());
        // The wizard's project list, billing and service-enablement gateways: every interface the Google build resolves resolves here too, so a ViewModel taking one never throws in fake mode only.
        services.AddSingleton<IProjectCatalogGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IBillingGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IServiceEnablementGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IBudgetGateway>(sp => sp.GetRequiredService<FakeGcp>());
        services.AddSingleton<IPricingCatalogGateway>(sp => sp.GetRequiredService<FakeGcp>());
    }
}

internal static class CloudAuditRegistration
{
    /// <summary>
    /// Issue #245: replaces each registered gateway that has a mutating call with an audited wrapper around the one that was there
    /// (the Resilient* wrapper over the fake, or the real Google gateway), so every mutation is one entry in the audit file whichever
    /// backend is built. Reads pass through. The log file is first touched when the first mutation happens.
    /// </summary>
    public static void Add(IServiceCollection services, AppDataRoot root)
    {
        services.AddSingleton<ICloudAuditLog>(_ => new JsonLinesCloudAuditLog(root.CloudAuditLogFile));
        services.AddSingleton<CloudAuditor>(sp => new CloudAuditor(
            sp.GetRequiredService<ICloudAuditLog>(),
            () =>
            {
                try
                {
                    return InstallationId.GetOrCreate(sp.GetRequiredService<ISettingsStore>());
                }
                catch (InstallationIdUnusableException)
                {
                    return null;
                }
            },
            sp.GetRequiredService<TimeProvider>()));

        Decorate<IComputeGateway>(services, (inner, auditor) => new AuditedComputeGateway(inner, auditor));
        Decorate<IStorageGateway>(services, (inner, auditor) => new AuditedStorageGateway(inner, auditor));
        Decorate<IProjectSetupGateway>(services, (inner, auditor) => new AuditedProjectSetupGateway(inner, auditor));
        Decorate<IWorkerIdentityGateway>(services, (inner, auditor) => new AuditedWorkerIdentityGateway(inner, auditor));
        Decorate<IServiceEnablementGateway>(services, (inner, auditor) => new AuditedServiceEnablementGateway(inner, auditor));
        Decorate<IBillingGateway>(services, (inner, auditor) => new AuditedBillingGateway(inner, auditor));
        Decorate<IProjectCatalogGateway>(services, (inner, auditor) => new AuditedProjectCatalogGateway(inner, auditor));
        Decorate<IQuotaGateway>(services, (inner, auditor) => new AuditedQuotaGateway(inner, auditor));
        Decorate<IPricingCatalogGateway>(services, (inner, auditor) => new AuditedPricingCatalogGateway(inner, auditor));
        Decorate<IBudgetGateway>(services, (inner, auditor) => new AuditedBudgetGateway(inner, auditor));
    }

    private static void Decorate<T>(IServiceCollection services, Func<T, CloudAuditor, T> wrap)
        where T : class
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered, so it cannot be audited.");
        var factory = existing.ImplementationFactory
            ?? throw new InvalidOperationException($"{typeof(T).Name} must be registered with a factory so the audit can wrap it.");
        services.Remove(existing);
        services.Add(new ServiceDescriptor(typeof(T), sp => wrap((T)factory(sp), sp.GetRequiredService<CloudAuditor>()), existing.Lifetime));
    }
}

/// <summary>Which cloud the container is built over: the real Google gateways (every shipped build), or the scripted fake (tests, and developer mode, issue #69).</summary>
public enum CloudBackend
{
    Google,
    Fake,

    /// <summary>The connected fake with its own signed-in account and worker image: <c>--fake-cloud</c> (issue #69).</summary>
    Developer,
}
