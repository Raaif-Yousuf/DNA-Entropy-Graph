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

    /// <summary>Calls inside <see cref="BreakerSamplingDuration"/> before the breaker may open at all.</summary>
    public int BreakerMinimumThroughput { get; init; } = 6;

    public double BreakerFailureRatio { get; init; } = 0.8;

    public TimeSpan BreakerSamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
