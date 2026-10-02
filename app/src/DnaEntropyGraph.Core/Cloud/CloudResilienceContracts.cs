namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Refreshes the signed-in account's access token. Called once by the
/// resilience pipeline (issue #258) when a gateway call fails with HTTP 401;
/// the real implementation lives in DnaEntropyGraph.Cloud next to the OAuth
/// flow (Hard Rule 7), so Core only names the seam.
/// </summary>
public interface ICloudTokenRefresher
{
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>One retry the resilience pipeline performed: which call, which attempt, why, and how long it waited first.</summary>
public sealed record CloudRetryEvent(string Operation, int Attempt, TimeSpan Delay, int? HttpStatus, CloudErrorKind Kind, string Message);

/// <summary>
/// Where the resilience pipeline reports what it did (issue #258: "two
/// retries logged"). The message in a <see cref="CloudRetryEvent"/> is the
/// API error text and never carries a sequence, file name or email
/// (CLAUDE.md Stack row "Logs").
/// </summary>
public interface ICloudCallObserver
{
    void OnRetry(CloudRetryEvent retry);

    void OnTokenRefreshed(string operation);

    /// <summary>The breaker opened (<paramref name="offline"/> true) or closed; this is what an offline banner reads.</summary>
    void OnConnectivityChanged(bool offline);
}
