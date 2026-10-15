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

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        TokenReads++;
        return FailWith is { } failure ? Task.FromException<string>(failure) : Task.FromResult(CurrentToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Refreshes++;
        _generation++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The real Google gateways built exactly the way production builds them (token source, resilience pipeline, the
/// <see cref="GoogleCloudGateways"/> factory) over a scripted HTTP handler, with every wait instant. The fixtures that
/// feed it are Google's documented REST response and google.rpc.Status error shapes, NOT captured from a live project:
/// there is no GCP access in this build (see docs/ToTest.md for what only a real project can prove).
/// </summary>
internal sealed class GoogleGatewayHarness
{
    public GoogleGatewayHarness(TimeSpan? operationDeadline = null, int retries = 3, string? installationId = "installation-1", int retentionDays = 30, string? selectedProjectId = "my-lab")
    {
        Tokens = new StubTokenSource();
        Handler = new ScriptedHttpHandler();
        Log = new CloudRetryLog();
        Pipeline = new CloudCallPipeline(
            new CloudRetryOptions { MaxRetryAttempts = retries, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
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
                OperationDeadline = operationDeadline ?? TimeSpan.FromMinutes(2),
                InstallationId = installationId,
                AppVersion = "0.1.0",
                ResultsRetentionDays = retentionDays,
                Random = new Random(53),
            },
            () => selectedProjectId);
    }

    public StubTokenSource Tokens { get; }

    public ScriptedHttpHandler Handler { get; }

    public CloudRetryLog Log { get; }

    public CloudCallPipeline Pipeline { get; }

    public GoogleCloudGatewaySet Gateways { get; }

    public List<TimeSpan> Delays { get; } = [];
}
