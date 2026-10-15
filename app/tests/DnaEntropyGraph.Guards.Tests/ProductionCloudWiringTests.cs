using System.Net;
using System.Text;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #609's observables, through the production registration: a real build resolves the Google-backed gateways, never
/// <see cref="FakeGcp"/>, and the first Compute request a run makes leaves for compute.googleapis.com. A container that
/// still resolves the fake passes every other test and ships a product that "completes" nothing.
/// </summary>
public class ProductionCloudWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-prodcloud-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // The temp folder is not worth failing a test over.
            }
        }
    }

    private ServiceProvider Build(Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root);
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static TheoryData<Type> CloudInterfaces() => new()
    {
        typeof(IComputeGateway),
        typeof(IStorageGateway),
        typeof(IWorkerIdentityGateway),
        typeof(IProjectSetupGateway),
        typeof(IQuotaGateway),
        typeof(IProjectCatalogGateway),
        typeof(IBillingGateway),
        typeof(IServiceEnablementGateway),
    };

    [Fact]
    public void The_interface_list_is_not_vacuous()
    {
        CloudInterfaces().Count.ShouldBeGreaterThanOrEqualTo(8);
    }

    [Theory]
    [MemberData(nameof(CloudInterfaces))]
    public void No_cloud_interface_of_the_production_container_is_backed_by_the_fake(Type serviceType)
    {
        using var provider = Build();

        var resolved = provider.GetRequiredService(serviceType);

        resolved.ShouldNotBeOfType<FakeGcp>($"{serviceType.Name} must be the Google-backed gateway in a real build");
        resolved.GetType().Name.ShouldNotStartWith("Fake");
    }

    [Fact]
    public void FakeGcp_is_not_registered_at_all_in_the_production_container()
    {
        using var provider = Build();

        provider.GetService<FakeGcp>().ShouldBeNull();
    }

    public static TheoryData<Type, string> UnwrappedGateways() => new()
    {
        { typeof(IComputeGateway), "DnaEntropyGraph.Cloud.Rest.GoogleComputeGateway" },
        { typeof(IStorageGateway), "DnaEntropyGraph.Cloud.Rest.GoogleStorageGateway" },
        { typeof(IWorkerIdentityGateway), "DnaEntropyGraph.Cloud.Rest.GoogleIamGateway" },
        { typeof(IProjectCatalogGateway), "DnaEntropyGraph.Cloud.Rest.GoogleProjectCatalogGateway" },
        { typeof(IServiceEnablementGateway), "DnaEntropyGraph.Cloud.Rest.GoogleServiceUsageGateway" },
    };

    [Theory]
    [MemberData(nameof(UnwrappedGateways))]
    public void The_real_gateways_that_run_their_own_pipeline_are_exactly_the_real_type_never_wrapped_a_second_time(Type serviceType, string expected)
    {
        using var provider = Build();

        // A Resilient* decorator around a gateway whose every HTTP call already goes through the pipeline would replay a whole
        // create on a transient error (GoogleComputeGateway and GoogleStorageGateway class docs).
        provider.GetRequiredService(serviceType).GetType().FullName.ShouldBe(expected);
    }
    [Fact]
    public void The_token_refresher_is_the_real_account_and_the_token_source_is_the_same_account()
    {
        using var provider = Build();

        var refresher = provider.GetRequiredService<ICloudTokenRefresher>();

        refresher.ShouldBeOfType<GoogleAccountService>();
        provider.GetRequiredService<IGcpAccessTokenSource>().ShouldBeSameAs(refresher);
        provider.GetRequiredService<IGcpAccount>().ShouldBeSameAs(refresher);
    }

    [Fact]
    public void A_signed_in_account_with_no_chosen_project_answers_no_project()
    {
        // The stopgap "fake-project" is gone: no chosen project means no project, and the run fails with the no-project error.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the seeded token file is DPAPI-protected");
        var auth = Path.Combine(_root, "auth");
        Directory.CreateDirectory(auth);
        File.WriteAllBytes(Path.Combine(auth, "1001.tok"), new DpapiSecretProtector().Protect(Encoding.UTF8.GetBytes("{\"access_token\":\"a\",\"refresh_token\":\"r\"}")));
        File.WriteAllText(
            Path.Combine(auth, "accounts.json"),
            "{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"email\":\"user@example.test\",\"needsSignIn\":false}]}");
        using var provider = Build();

        var account = provider.GetRequiredService<IGcpAccount>();

        account.IsSignedIn.ShouldBeTrue();
        account.SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public async Task A_run_started_signed_in_with_no_project_chosen_fails_no_project_not_with_a_fake_success()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the seeded token file is DPAPI-protected");
        var auth = Path.Combine(_root, "auth");
        Directory.CreateDirectory(auth);
        File.WriteAllBytes(Path.Combine(auth, "1001.tok"), new DpapiSecretProtector().Protect(Encoding.UTF8.GetBytes("{\"access_token\":\"a\",\"refresh_token\":\"r\"}")));
        File.WriteAllText(
            Path.Combine(auth, "accounts.json"),
            "{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"email\":\"user@example.test\",\"needsSignIn\":false}]}");
        var input = Path.Combine(_root, "seq.fa");
        File.WriteAllText(input, ">s\nACGTACGTACGTACGTACGT\n");
        await using var provider = Build();
        // Signed out also answers NoProject: the seeded token must have been read, or this test proves nothing.
        provider.GetRequiredService<IGcpAccount>().IsSignedIn.ShouldBeTrue();

        var jobId = await provider.GetRequiredService<IJobEngine>().StartRunAsync(
            new DnaEntropyGraph.Core.RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", InputPath = input },
            CancellationToken.None);

        var run = (await provider.GetRequiredService<IRunRepository>().GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        run.ErrorCode.ShouldBe(RunErrorCodes.NoProject);
    }

    [Fact]
    public void An_installation_id_that_was_unusable_at_first_use_is_read_again_after_the_user_repairs_the_file()
    {
        // Issue #609 cold review: the options were built once, so one failed read left the id null for the whole process.
        var reads = 0;
        var settings = Substitute.For<ISettingsStore>();
        settings.GetString(Arg.Any<string>()).Returns(_ =>
        {
            if (reads++ == 0)
            {
                throw new InstallationIdUnusableException("the id file is unreadable");
            }

            return "0123456789abcdef0123456789abcdef";
        });
        using var provider = Build(services => services.AddSingleton(settings));

        var options = provider.GetRequiredService<GoogleCloudOptions>();

        options.CurrentInstallationId.ShouldBeNull("the first read failed");
        options.CurrentInstallationId.ShouldBe("0123456789abcdef0123456789abcdef", "the file was repaired: the next use reads it again");
    }

    public static TheoryData<CloudBackend> Backends() => new() { CloudBackend.Google, CloudBackend.Fake, CloudBackend.Developer };

    [Theory]
    [MemberData(nameof(Backends))]
    public void Every_cloud_gateway_interface_of_Core_resolves_in_every_backend(CloudBackend backend)
    {
        var gateways = typeof(IComputeGateway).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace == "DnaEntropyGraph.Core.Cloud" && t.Name.EndsWith("Gateway", StringComparison.Ordinal))
            .ToList();
        gateways.Count.ShouldBeGreaterThanOrEqualTo(8, "the scan must find the gateways, or this test is vacuous");
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: backend);
        using var provider = services.BuildServiceProvider();

        foreach (var gateway in gateways)
        {
            provider.GetService(gateway).ShouldNotBeNull($"{gateway.Name} does not resolve in {backend}: a ViewModel taking it would throw there");
        }
    }
    [Fact]
    public async Task With_a_project_chosen_the_first_compute_request_goes_to_compute_googleapis_com_with_the_account_token()
    {
        var handler = new RecordingHandler();
        var tokens = Substitute.For<IGcpAccessTokenSource>();
        tokens.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("scripted-token");
        var account = Substitute.For<IGcpAccount>();
        account.SelectedProjectId.Returns("chosen-project");
        using var provider = Build(services =>
        {
            // The scripted HTTP handler replaces the network (the same seam the Cloud.Tests use); the token source and the
            // account are the only other things stubbed, so the gateway, the pipeline and the project lookup are the real ones.
            services.AddSingleton(new GoogleCloudOptions { HttpHandler = handler });
            services.AddSingleton(tokens);
            services.AddSingleton(account);
        });

        var found = await provider.GetRequiredService<IComputeGateway>().ListAppVmsAsync(CancellationToken.None);

        found.ShouldBeEmpty();
        handler.Requests.ShouldNotBeEmpty("the observable: a request was made at all");
        handler.Requests[0].Host.ShouldBe("compute.googleapis.com");
        handler.Requests[0].AbsolutePath.ShouldContain("/projects/chosen-project/");
        handler.Authorization[0].ShouldBe("Bearer scripted-token");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public List<string?> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
