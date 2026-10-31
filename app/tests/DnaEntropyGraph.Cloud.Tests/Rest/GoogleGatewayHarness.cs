using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>A token source that also refreshes: the token changes on <see cref="RefreshAsync"/>, so a replayed call is visible in the recorded Authorization header.</summary>
internal sealed class StubTokenSource : IGcpAccessTokenSource, ICloudTokenRefresher
{
    private int _generation = 1;

    public int TokenReads { get; private set; }

    public int Refreshes { get; private set; }

    public Exception? FailWith { get; set; }

    public string CurrentToken => $"ya29.test-token-{_generation}";

    public Task<string> GetAccessTokenAsync(string? accountSub, CancellationToken cancellationToken)
    {
        TokenReads++;
        return FailWith is { } failure ? Task.FromException<string>(failure) : Task.FromResult(CurrentToken);
    }

    public Task RefreshAsync(string? accountSub, CancellationToken cancellationToken)
    {
        Refreshes++;
        _generation++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The real Google gateways built exactly the way production builds them (token source, resilience pipeline, the
/// <see cref="GoogleCloudGateways"/> factory) over a scripted HTTP handler, with every wait instant and every deadline on a
/// clock that stands still (#567): <see cref="Clock"/> moves only when a call the test scripted to hang is parked, by the
/// operation deadline, so a loaded machine can neither cut a healthy call nor shave the budget the waits add up to. The fixtures that
/// feed it are Google's documented REST response and google.rpc.Status error shapes, NOT captured from a live project:
/// there is no GCP access in this build (see docs/ToTest.md for what only a real project can prove).
/// </summary>
internal sealed class GoogleGatewayHarness
{
    public GoogleGatewayHarness(TimeSpan? operationDeadline = null, int retries = 3, string? installationId = "installation-1", int retentionDays = 30, string? selectedProjectId = "my-lab", TimeProvider? retryClock = null, TimeSpan? attemptTimeout = null, TimeProvider? time = null, Func<int>? retentionSource = null)
    {
        Tokens = new StubTokenSource();
        var deadline = operationDeadline ?? TimeSpan.FromMinutes(2);
        Handler = new ScriptedHttpHandler { OnHang = () => Clock.Advance(deadline + TimeSpan.FromMilliseconds(1)) };
        Log = new CloudRetryLog();
        Pipeline = new CloudCallPipeline(
            new CloudRetryOptions
            {
                MaxRetryAttempts = retries,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                TimeProvider = retryClock ?? TimeProvider.System,
                AttemptTimeout = attemptTimeout ?? new CloudRetryOptions().AttemptTimeout,
            },
            Tokens,
            Log);
        Gateways = GoogleCloudGateways.Create(
            Tokens,
            Pipeline,
            new GoogleCloudOptions
            {
                HttpHandler = Handler,
                Delay = (span, _) =>
                {
                    Delays.Add(span);
                    return Task.CompletedTask;
                },
                TimeProvider = time ?? Clock,
                OperationDeadline = deadline,
                InstallationId = installationId,
                AppVersion = "0.1.0",
                ResultsRetentionDays = retentionDays,
                ResultsRetentionDaysSource = retentionSource,
                Random = new Random(53),
            },
            () => selectedProjectId);
    }

    public StubTokenSource Tokens { get; }

    /// <summary>The clock every deadline of the gateways runs on. It never moves by itself.</summary>
    public VirtualTimeProvider Clock { get; } = new();

    public ScriptedHttpHandler Handler { get; }

    public CloudRetryLog Log { get; }

    public CloudCallPipeline Pipeline { get; }

    public GoogleCloudGatewaySet Gateways { get; }

    public List<TimeSpan> Delays { get; } = [];
}
