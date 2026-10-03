using System.Reflection;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #258: one resilience pipeline in front of every gateway. Transient
/// failures (HTTP 429 that is not a quota, 5xx, transport) are retried with
/// jitter and then given up on with a NETWORK code; a 401 refreshes the
/// token once; a run of failures opens a breaker the offline banner reads.
/// FakeGcp is the double, so these prove the pipeline against exactly the
/// exception shape a real gateway must throw.
/// </summary>
public class ResilientGatewayTests
{
    private const string Project = "my-project";

    private sealed class CountingRefresher : ICloudTokenRefresher
    {
        public int Count { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(_ticks);

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static CloudRetryOptions FastOptions(int retries = 4, TimeProvider? time = null) => new()
    {
        MaxRetryAttempts = retries,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        TimeProvider = time ?? TimeProvider.System,
    };

    private sealed class Rig
    {
        public Rig(FakeGcp gcp, CloudRetryOptions options)
        {
            Gcp = gcp;
            Log = new CloudRetryLog();
            Refresher = new CountingRefresher();
            Pipeline = new CloudCallPipeline(options, Refresher, Log);
            Compute = new ResilientComputeGateway(gcp, Pipeline);
        }

        public FakeGcp Gcp { get; }

        public CloudRetryLog Log { get; }

        public CountingRefresher Refresher { get; }

        public CloudCallPipeline Pipeline { get; }

        public IComputeGateway Compute { get; }
    }

    private static VmSpec Spec(string jobId = "job-1") => new(
        Project, "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE");

    [Fact]
    public async Task Two_503s_then_success_completes_with_two_retries_logged()
    {
        var rig = new Rig(new FakeGcp().WithTransientFailures(503, 2), FastOptions());

        var vm = await rig.Compute.CreateVmAsync(Spec(), "us-central1-a", CancellationToken.None);

        vm.Status.ShouldBe("RUNNING");
        rig.Log.Retries.Count.ShouldBe(2);
        rig.Log.Retries.Select(r => r.Attempt).ShouldBe([1, 2]);
        rig.Log.Retries.ShouldAllBe(r => r.HttpStatus == 503 && r.Operation == "Compute.CreateVm");
    }

    [Fact]
    public async Task Giving_up_throws_a_network_error_after_the_configured_number_of_retries()
    {
        var rig = new Rig(new FakeGcp().WithTransientFailures(503, 100), FastOptions(retries: 2));

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Compute.GetVmAsync("deg-job-1", "us-central1-a", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Network);
        ex.Error.HttpStatus.ShouldBe(503);
        rig.Log.Retries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_rate_limit_429_is_retried()
    {
        var rig = new Rig(new FakeGcp().WithTransientFailures(429, 1, "Rate Limit Exceeded"), FastOptions());

        await rig.Compute.GetVmAsync("deg-job-1", "us-central1-a", CancellationToken.None);

        rig.Log.Retries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_429_that_is_a_quota_is_not_retried_because_waiting_cannot_fix_it()
    {
        var rig = new Rig(new FakeGcp().WithTransientFailures(429, 5, "Quota exceeded for quota metric 'GPUS_ALL_REGIONS'", "QUOTA_EXCEEDED"), FastOptions());

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Compute.GetVmAsync("deg-job-1", "us-central1-a", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Quota);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_403_is_not_retried()
    {
        var gcp = new FakeGcp().WithPermissionDenied(Project);
        var rig = new Rig(gcp, FastOptions());

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Compute.CreateVmAsync(Spec(), "us-central1-a", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transport_failure_is_retried()
    {
        var rig = new Rig(new FakeGcp().WithNetworkFailures(2), FastOptions());

        var vm = await rig.Compute.CreateVmAsync(Spec(), "us-central1-a", CancellationToken.None);

        vm.Status.ShouldBe("RUNNING");
        rig.Log.Retries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_once_and_then_succeeds()
    {
        var rig = new Rig(new FakeGcp().WithUnauthorized(1), FastOptions());

        await rig.Compute.GetVmAsync("deg-job-1", "us-central1-a", CancellationToken.None);

        rig.Refresher.Count.ShouldBe(1);
        rig.Log.TokenRefreshes.ShouldBe(1);
        rig.Log.Retries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_401_after_a_refresh_is_a_permission_error_and_refreshes_only_once()
    {
        var rig = new Rig(new FakeGcp().WithUnauthorized(10), FastOptions());

        var ex = await Should.ThrowAsync<CloudOperationException>(
            () => rig.Compute.GetVmAsync("deg-job-1", "us-central1-a", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Refresher.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Repeated_failures_open_the_breaker_and_later_calls_fail_fast_until_it_closes()
    {
        var time = new ManualTimeProvider();
        var options = FastOptions(retries: 1, time: time) with
        {
            BreakerMinimumThroughput = 2,
            BreakerFailureRatio = 0.5,
            BreakerSamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30),
        };
        var gcp = new FakeGcp().WithTransientFailures(503, 2);
        var rig = new Rig(gcp, options);
        var offlineChanges = new List<bool>();
        rig.Pipeline.OfflineChanged += (_, _) => offlineChanges.Add(rig.Pipeline.IsOffline);

        // Two failing attempts (the call and its one retry) trip the breaker.
        await Should.ThrowAsync<CloudOperationException>(() => rig.Compute.GetVmAsync("v", "us-central1-a", CancellationToken.None));
        rig.Pipeline.IsOffline.ShouldBeTrue();

        // The fake has no failures left, so a call that reaches it would
        // succeed: this one must fail fast without reaching it.
        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Compute.GetVmAsync("v", "us-central1-a", CancellationToken.None));
        ex.Kind.ShouldBe(CloudErrorKind.Network);

        time.Advance(TimeSpan.FromSeconds(31));
        await rig.Compute.GetVmAsync("v", "us-central1-a", CancellationToken.None);

        rig.Pipeline.IsOffline.ShouldBeFalse();
        offlineChanges.ShouldBe([true, false]);
    }

    [Fact]
    public async Task Cancellation_stops_the_retry_loop()
    {
        var rig = new Rig(new FakeGcp().WithTransientFailures(503, 100), FastOptions(retries: 50));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => rig.Compute.GetVmAsync("v", "us-central1-a", cts.Token));
    }

    /// <summary>
    /// The decorators are hand-written, so a new interface method that
    /// forgets to go through the pipeline would compile and silently skip
    /// the retry policy. Arm one 503 and call every method of every gateway
    /// interface: each must log exactly one retry.
    /// </summary>
    [Fact]
    public async Task Every_method_of_every_gateway_goes_through_the_pipeline()
    {
        var failures = new List<string>();
        var interfaces = new[]
        {
            (typeof(IComputeGateway), (Func<FakeGcp, CloudCallPipeline, object>)((g, p) => new ResilientComputeGateway(g, p))),
            (typeof(IStorageGateway), (g, p) => new ResilientStorageGateway(g, p)),
            (typeof(IProjectSetupGateway), (g, p) => new ResilientProjectSetupGateway(g, p)),
            (typeof(IQuotaGateway), (g, p) => new ResilientQuotaGateway(g, p)),
            (typeof(IBillingGateway), (g, p) => new ResilientBillingGateway(g, p)),
        };

        var methodCount = 0;
        foreach (var (type, make) in interfaces)
        {
            foreach (var method in type.GetMethods())
            {
                methodCount++;
                var gcp = new FakeGcp().WithTransientFailures(503, 1);
                gcp.PutObject("x", "x", [1]); // Download of a missing object is a 404 now, as the real bucket would say
                var rig = new Rig(gcp, FastOptions());
                var gateway = make(gcp, rig.Pipeline);

                var args = method.GetParameters().Select(ArgumentFor).ToArray();
                var task = (Task)method.Invoke(gateway, args)!;
                await task;

                if (rig.Log.Retries.Count != 1)
                {
                    failures.Add($"{type.Name}.{method.Name} logged {rig.Log.Retries.Count} retries");
                }
            }
        }

        methodCount.ShouldBeGreaterThan(10);
        failures.ShouldBeEmpty();
    }

    /// <summary>
    /// The project catalog and service-usage gateways make a mutating POST plus polled reads. A wrapper that sends the
    /// whole call through one retry would replay the POST when a read fails, so the real gateways pipeline each HTTP
    /// call themselves and must never be wrapped. This fails if a wrapper is added to either one (by type name, so it
    /// cannot be satisfied by deleting the class and keeping the wrapping), and if no Resilient wrapper for them exists.
    /// </summary>
    [Fact]
    public void The_catalog_and_service_usage_gateways_are_never_wrapped_in_a_resilient_decorator()
    {
        var gateways = DnaEntropyGraph.Cloud.Rest.GoogleCloudGateways.Create(new Rest.StubTokenSource(), new CloudCallPipeline(FastOptions(), new FakeGcp(), new CloudRetryLog()));

        gateways.ProjectCatalog.GetType().Name.ShouldBe("GoogleProjectCatalogGateway");
        gateways.Services.GetType().Name.ShouldBe("GoogleServiceUsageGateway");
        typeof(CloudCallPipeline).Assembly.GetTypes()
            .Select(t => t.Name)
            .Where(n => n.StartsWith("Resilient", StringComparison.Ordinal) && (n.Contains("ProjectCatalog", StringComparison.Ordinal) || n.Contains("ServiceEnablement", StringComparison.Ordinal)))
            .ShouldBeEmpty("a Resilient wrapper for the catalog or service-usage gateway exists; wrapping them re-POSTs on a failed read");
        gateways.Billing.GetType().Name.ShouldBe("ResilientBillingGateway");
    }

    private static object? ArgumentFor(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (type == typeof(VmSpec))
        {
            return Spec();
        }

        if (type == typeof(Stream))
        {
            return new MemoryStream();
        }

        if (type == typeof(IReadOnlyList<string>))
        {
            return new[] { "x" };
        }

        return type == typeof(string) ? "x" : throw new InvalidOperationException($"No sample for {type}");
    }
}
