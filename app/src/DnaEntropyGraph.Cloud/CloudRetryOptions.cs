namespace DnaEntropyGraph.Cloud;

/// <summary>
/// Tuning for <see cref="CloudCallPipeline"/> (issue #258). Defaults retry a
/// transient failure four times (about 1, 2, 4, 8 seconds, jittered) and open
/// the breaker when most of the last 30 seconds of calls failed.
/// </summary>
public sealed record CloudRetryOptions
{
    public int MaxRetryAttempts { get; init; } = 4;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long one attempt of a SINGLE-REQUEST call may take before it is cut and retried (issue #600). Applied only to calls that
    /// pass <c>singleRequest: true</c> to <see cref="CloudCallPipeline.ExecuteAsync{T}"/>: the small metadata calls of the bucket setup.
    /// Never applied to a media transfer (upload or download of an input or output, of any size) nor to a call that wraps several requests.
    /// Without it a request that never answers is cut only by the caller's own deadline (the 5 minute bucket budget) or the HTTP client's 100 s default.
    /// </summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Calls inside <see cref="BreakerSamplingDuration"/> before the breaker may open at all.</summary>
    public int BreakerMinimumThroughput { get; init; } = 6;

    public double BreakerFailureRatio { get; init; } = 0.8;

    public TimeSpan BreakerSamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
