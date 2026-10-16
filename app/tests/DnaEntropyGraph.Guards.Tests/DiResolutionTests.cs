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
        // Self-maintaining: every concrete *ViewModel in Presentation is found by reflection and must resolve, so a new
        // ViewModel (or a Settings group) needs no number edited here. The floor only proves the scan found real types.
        viewModelTypes.Count.ShouldBeGreaterThanOrEqualTo(9, "Appendix A section 1 names 9 ViewModels; a scan that finds fewer is not reading Presentation.");

        foreach (var viewModelType in viewModelTypes)
        {
            provider.GetRequiredService(viewModelType).ShouldNotBeNull();
        }
    }

    [Fact]
    public void New_run_resolves_with_its_option_preset_picker_not_without_one()
    {
        // Issue #136's wired-to-nothing observable: the picker is an optional constructor argument, so a missing registration
        // would not fail resolution; the page would just show an empty picker. Presets must be there.
        using var provider = BuildRealServiceProvider();

        provider.GetRequiredService<DnaEntropyGraph.Presentation.ViewModels.NewRunViewModel>().Presets.ShouldNotBeNull();
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
            typeof(DnaEntropyGraph.Core.Abstractions.IFolderLauncher),
            typeof(DnaEntropyGraph.Core.Diagnostics.IDiagnosticsExporter),
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
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value, cloud: DnaEntropyGraph.App.Startup.CloudBackend.Fake);
        services.AddSingleton(new DnaEntropyGraph.Cloud.CloudRetryOptions { BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero });
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<DnaEntropyGraph.Cloud.FakeGcp>().WithTransientFailures(503, 2);

        var enabled = await provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IProjectSetupGateway>()
            .IsBillingEnabledAsync("my-project", CancellationToken.None);

        enabled.ShouldBeTrue();
        provider.GetRequiredService<DnaEntropyGraph.Cloud.CloudRetryLog>().Retries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_account_is_the_real_Google_sign_in_and_a_build_with_no_client_file_says_so_by_code()
    {
        // Issue #48's wired-to-nothing observable. With FakeGcp registered as IGcpAccount this resolves fine and
        // SignInAsync "succeeds"; only the real service reads oauth_client.local.json and refuses by name.
        using var provider = BuildRealServiceProvider();

        var account = provider.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IGcpAccount>();

        account.ShouldBeOfType<DnaEntropyGraph.Cloud.Auth.GoogleAccountService>();
        account.IsSignedIn.ShouldBeFalse();
        var failure = await Should.ThrowAsync<DnaEntropyGraph.Core.Cloud.AccountAuthException>(() => account.SignInAsync(CancellationToken.None));
        failure.Code.ShouldBe(DnaEntropyGraph.Core.Cloud.AuthErrorCodes.OAuthClientMissing);

        // The access-token source and the refresher are the real account (#609): a 401 from a real gateway refreshes the real token.
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IGcpAccessTokenSource>().ShouldBeSameAs(account);
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.ICloudTokenRefresher>().ShouldBeSameAs(account);
    }

    [Fact]
    public async Task A_signed_in_production_run_gets_past_the_project_check()
    {
        // Cold review of #48: replacing the fake account made SelectedProjectId null, so every run failed no_project
        // ("choose a project in Settings", a control that does not exist) instead of reaching the not-connected gateways.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the seeded token file is DPAPI-protected");
        var root = Path.Combine(Path.GetTempPath(), $"deg-guard-signedin-{Guid.NewGuid():n}");
        var auth = Path.Combine(root, "auth");
        Directory.CreateDirectory(auth);
        try
        {
            File.WriteAllBytes(
                Path.Combine(auth, "1001.tok"),
                new DnaEntropyGraph.Cloud.Auth.DpapiSecretProtector().Protect(System.Text.Encoding.UTF8.GetBytes("{\"access_token\":\"a\",\"refresh_token\":\"r\"}")));
            File.WriteAllText(
                Path.Combine(auth, "accounts.json"),
                "{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"email\":\"user@example.test\",\"needsSignIn\":false}]}");
            var input = Path.Combine(root, "seq.fa");
            File.WriteAllText(input, ">s\nACGTACGTACGTACGTACGT\n");

            var services = new ServiceCollection();
            services.AddDnaEntropyGraph(appDataRoot: root, cloud: DnaEntropyGraph.App.Startup.CloudBackend.Fake);
            await using var provider = services.BuildServiceProvider();
            var account = provider.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IGcpAccount>();
            account.IsSignedIn.ShouldBeTrue();
            account.SelectedProjectId.ShouldNotBeNullOrWhiteSpace();

            var jobId = await provider.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IJobEngine>().StartRunAsync(
                new DnaEntropyGraph.Core.RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", InputPath = input },
                CancellationToken.None);

            var run = (await provider.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IRunRepository>().GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
            run.ErrorCode.ShouldNotBe(DnaEntropyGraph.Core.Cloud.RunErrorCodes.NoProject);
            // MEASURED 2026-10-02: the shipped image list pins no digest, so the image check, which follows the
            // project check, answers first. When an image is pinned the next stop is the not-connected gateways
            // (cloud_not_connected): change this assertion deliberately then. no_project is the false answer.
            // StartRunAsync awaits the repository write before it returns, so reading here is not a race.
            run.ErrorCode.ShouldBe(DnaEntropyGraph.Core.Cloud.RunErrorCodes.WorkerImageUnavailable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Theory]
    [InlineData(DnaEntropyGraph.App.Startup.CloudBackend.Google)]
    [InlineData(DnaEntropyGraph.App.Startup.CloudBackend.Fake)]
    [InlineData(DnaEntropyGraph.App.Startup.CloudBackend.Developer)]
    public void The_wizard_and_its_setup_service_resolve_from_the_real_container_in_every_cloud_backend(DnaEntropyGraph.App.Startup.CloudBackend backend)
    {
        // Cold review of #99 doubted that the project, billing, service and worker-identity gateways behind ISetupCheckService are
        // registered; they are (#609), for every backend. This keeps it true: the wizard is built by the real registration.
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value, cloud: backend);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IProjectCatalogGateway>().ShouldNotBeNull();
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IBillingGateway>().ShouldNotBeNull();
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IServiceEnablementGateway>().ShouldNotBeNull();
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IWorkerIdentityGateway>().ShouldNotBeNull();
        provider.GetRequiredService<DnaEntropyGraph.Core.Setup.ISetupCheckService>().ShouldBeOfType<DnaEntropyGraph.Core.Setup.SetupCheckService>();
        var wizard = provider.GetRequiredService<DnaEntropyGraph.Presentation.ViewModels.WizardViewModel>();
        wizard.SetupSteps.ShouldNotBeNull();
        wizard.ProjectRow.ShouldNotBeNull();
    }

    [Fact]
    public void The_real_google_composition_hands_the_gateway_the_saved_results_retention_at_each_use()
    {
        // Issue #114's wired-to-nothing observable: the options the real storage gateway is built from read the retention setting.
        // Before the fix nothing set ResultsRetentionDays, so the bucket rule stayed at 90 whatever the setting said.
        var root = Path.Combine(Path.GetTempPath(), "deg-114-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddDnaEntropyGraph(appDataRoot: root, cloud: DnaEntropyGraph.App.Startup.CloudBackend.Google);
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<DnaEntropyGraph.Cloud.Rest.GoogleCloudOptions>();
            var settings = provider.GetRequiredService<DnaEntropyGraph.Core.Abstractions.ISettingsStore>();

            options.ResultsRetentionDays.ShouldBe(90);
            settings.SetString(DnaEntropyGraph.Core.Cloud.ResultsRetentionSetting.Key, "30");
            options.ResultsRetentionDays.ShouldBe(30);

            // The Settings control itself: choosing a length in the real ViewModel changes what the gateway's options read.
            var vm = provider.GetRequiredService<DnaEntropyGraph.Presentation.ViewModels.CloudResultsSettingsViewModel>();
            vm.SelectedChoice = vm.Choices.Single(c => c.Days == 180);
            options.ResultsRetentionDays.ShouldBe(180);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task The_run_completion_path_reaches_the_registered_desktop_notifier_and_a_click_is_routed()
    {
        // Issue #107, wired-to-nothing: the Windows notifier and the activation router are the real App types, and the completion
        // path the shell calls really hands a finished run to whatever IDesktopNotifier is registered.
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value, cloud: DnaEntropyGraph.App.Startup.CloudBackend.Fake);
        using (var real = services.BuildServiceProvider())
        {
            real.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IDesktopNotifier>().ShouldBeOfType<DnaEntropyGraph.App.Services.AppNotifier>();
            real.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IAppWindow>().ShouldBeSameAs(real.GetRequiredService<DnaEntropyGraph.App.Services.AppWindowService>());
            real.GetRequiredService<DnaEntropyGraph.Presentation.Services.ActivationRouter>().ShouldNotBeNull();
            real.GetRequiredService<DnaEntropyGraph.App.Services.INotificationSink>().ShouldBeOfType<DnaEntropyGraph.App.Services.WindowsNotificationSink>();
        }

        var seen = new List<(string Title, string? JobId)>();
        services.AddSingleton<DnaEntropyGraph.Core.Abstractions.IDesktopNotifier>(new RecordingNotifier(seen));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<DnaEntropyGraph.Presentation.Services.RunCompletionActions>().OnRunFailedAsync("job-42");

        seen.Count.ShouldBe(1);
        seen[0].JobId.ShouldBe("job-42");
    }

    private sealed class RecordingNotifier(List<(string Title, string? JobId)> seen) : DnaEntropyGraph.Core.Abstractions.IDesktopNotifier
    {
        public void Notify(string title, string body, string? jobId) => seen.Add((title, jobId));
    }

    [Fact]
    public async Task An_open_with_file_reaches_the_New_run_screen_the_container_builds_and_waits_for_the_shell()
    {
        // Issue #108, wired-to-nothing: the router, the intake and the page's ViewModel share the one registered intake and readiness.
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value, cloud: DnaEntropyGraph.App.Startup.CloudBackend.Fake);
        await using var provider = services.BuildServiceProvider();
        var readiness = provider.GetRequiredService<DnaEntropyGraph.Presentation.Services.ShellReadiness>();
        var viewModel = provider.GetRequiredService<DnaEntropyGraph.Presentation.ViewModels.NewRunViewModel>();
        await viewModel.ReceiveNavigationParameterCommand.ExecuteAsync(null);

        provider.GetRequiredService<DnaEntropyGraph.Presentation.Services.NewRunIntake>()
            .TryAdd(new DnaEntropyGraph.Presentation.ViewModels.DroppedItems([], 0, false)).ShouldBeTrue("the page's ViewModel attached to the registered intake");
        readiness.IsReady.ShouldBeFalse("nothing but the window's Loaded handler marks the shell ready");
        provider.GetRequiredService<DnaEntropyGraph.Presentation.Services.ActivationRouter>().ShouldNotBeNull();
    }

    [Fact]
    public void The_real_production_container_builds_with_no_missing_registration()
    {
        Should.NotThrow(() =>
        {
            using var provider = BuildRealServiceProvider();
        });
    }

    [Fact]
    public void Open_in_igv_and_open_in_geneious_are_registered_real_services_and_are_bound_by_the_results_page()
    {
        using var provider = BuildRealServiceProvider();

        // Issue #586, wired-to-nothing: the opener, its socket client, its locator and its process launcher resolve to the
        // production types, and each button's command is a property of the ViewModel that ResultsPage.xaml binds.
        provider.GetRequiredService<DnaEntropyGraph.Presentation.Services.IExternalViewerOpener>()
            .ShouldBeOfType<DnaEntropyGraph.Presentation.Services.ExternalViewerOpener>();
        provider.GetRequiredService<DnaEntropyGraph.Core.Viewers.IIgvBatchClient>().ShouldBeOfType<DnaEntropyGraph.Core.Viewers.TcpIgvBatchClient>();
        provider.GetRequiredService<DnaEntropyGraph.Core.Viewers.IViewerLocator>().ShouldBeOfType<DnaEntropyGraph.Core.Viewers.ViewerLocator>();
        provider.GetRequiredService<DnaEntropyGraph.Core.Viewers.IViewerProcessLauncher>().ShouldNotBeNull();

        var xaml = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Views", "ResultsPage.xaml"));
        foreach (var command in new[] { "OpenInIgvCommand", "OpenInGeneiousCommand" })
        {
            typeof(DnaEntropyGraph.Presentation.ViewModels.ResultsViewModel).GetProperty(command).ShouldNotBeNull(command);
            xaml.ShouldContain("{x:Bind ViewModel." + command + "}");
        }
    }

    [Theory]
    [InlineData(CloudBackend.Google)]
    [InlineData(CloudBackend.Fake)]
    [InlineData(CloudBackend.Developer)]
    public void The_Cloud_page_gateway_pieces_resolve_in_every_backend(CloudBackend backend)
    {
        // Issue #105: InstallationCleanup and StartVmAsync have no caller yet (the app lane binds them); a registration that fails
        // would only show as an exception on the first Cloud page open.
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value, cloud: backend);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.InstallationCleanup>().ShouldNotBeNull();
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IComputeGateway>().ShouldNotBeNull();
        provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IStorageGateway>().ShouldNotBeNull();
    }

    [Fact]
    public async Task The_registered_cleanup_ends_the_row_of_a_run_whose_VM_it_deletes_and_StartVm_reaches_the_gateway()
    {
        // The observable that differs if the run repository and the driver registry were not wired into the cleanup: the row stays Running.
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: TempAppDataRoot.Value, cloud: CloudBackend.Developer);
        using var provider = services.BuildServiceProvider();
        var gcp = provider.GetRequiredService<DnaEntropyGraph.Cloud.FakeGcp>();
        var spec = new DnaEntropyGraph.Core.Cloud.VmSpec(
            ProjectId: "fake-project",
            InstallationId: "inst-di",
            JobId: "job-di",
            Model: "evo2_7b",
            AppVersion: "0.1.0",
            Lifecycle: "stop",
            MachineType: "g2-standard-8",
            MaxRunDuration: TimeSpan.FromHours(4),
            TerminationAction: "DELETE");
        await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);
        var compute = provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.IComputeGateway>();
        await compute.StopVmAsync("deg-job-di", "us-central1-a", CancellationToken.None);
        await compute.StartVmAsync("deg-job-di", "us-central1-a", CancellationToken.None);
        (await compute.GetVmAsync("deg-job-di", "us-central1-a", CancellationToken.None))!.Status.ShouldBe("RUNNING");
        var runs = provider.GetRequiredService<DnaEntropyGraph.Core.Abstractions.IRunRepository>();
        await runs.UpsertAsync(new DnaEntropyGraph.Core.Abstractions.RunRecord("job-di", DnaEntropyGraph.Core.JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);

        await provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.InstallationCleanup>().DeleteAllAsync("fake-project", "inst-di", CancellationToken.None);

        var row = (await runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == "job-di");
        row.Phase.ShouldBe(DnaEntropyGraph.Core.JobPhase.Cancelled);
        row.ErrorCode.ShouldBe(DnaEntropyGraph.Core.Cloud.RunErrorCodes.DeletedByCleanup);
    }
    [Fact]
    public async Task The_production_runner_has_safe_stall_and_notice_defaults_that_need_no_UI()
    {
        // Issue #675: OnStall / OnNotice / CancelAckTimeout were wired to nothing in production. A stall with no UI must resolve by itself.
        using var provider = BuildRealServiceProvider();
        var runner = provider.GetRequiredService<DnaEntropyGraph.Core.Cloud.CloudJobRunner>();

        runner.OnStall.ShouldNotBeNull();
        var choice = await runner.OnStall!(new DnaEntropyGraph.Core.Cloud.StallPrompt("job-1", "worker_stage_stuck", "stuck", "model-loading", TimeSpan.FromMinutes(20)), CancellationToken.None);
        choice.ShouldBe(DnaEntropyGraph.Core.Cloud.StallChoice.EndAsRunChose);
        runner.OnNotice.ShouldNotBeNull();
        Should.NotThrow(() => runner.OnNotice!(new DnaEntropyGraph.Core.Cloud.RunNotice("job-1", DnaEntropyGraph.Core.Cloud.RunNoticeKind.SlowStage, "model-loading")));
        runner.CancelAckTimeout.ShouldBe(TimeSpan.FromSeconds(60));
    }
}
