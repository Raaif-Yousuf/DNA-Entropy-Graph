namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #256. Compute is operation-based: a mutation (Insert, Stop,
/// Delete) returns an operation, and the real error - stockout, quota,
/// whatever - lives in the polled operation, not the initial response.
/// This helper is the one place that polls-with-backoff, so every caller
/// (today: <see cref="CloudJobRunner"/>'s provisioning step; eventually a
/// real, non-fake gateway polling an actual Google LRO) gets the same
/// exponential backoff (1s doubling to a 10s cap) and the same
/// deadline-vs-genuine-error distinction, instead of each caller inventing
/// its own retry loop.
///
/// <paramref name="poll"/> is called immediately, then - if it reports
/// "not done yet" - again after a backoff delay, until it reports done or
/// the deadline is spent. A deadline that expires before the operation
/// reports done is itself surfaced as a <see cref="CloudError"/> (code
/// <c>OPERATION_POLL_TIMEOUT</c>), distinct from - and never confused
/// with - a real error the operation itself reported, per this issue's own
/// observable: "an insert that fails with ZONE_RESOURCE_POOL_EXHAUSTED
/// surfaces that code from the polled operation, not a generic timeout."
///
/// No real time is ever awaited unless the caller's own <paramref
/// name="delay"/> (default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>)
/// does so - a test supplies an instant, recording delay function so the
/// whole backoff/deadline algorithm is exercised in milliseconds.
/// </summary>
public static class OperationPoller
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    public static async Task<OperationOutcome<T>> PollAsync<T>(
        Func<CancellationToken, Task<OperationPoll<T>>> poll,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(poll);

        var wait = delay ?? ((span, ct) => Task.Delay(span, ct));
        var backoff = InitialBackoff;
        var elapsed = TimeSpan.Zero;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await poll(cancellationToken).ConfigureAwait(false);
            if (snapshot.IsDone)
            {
                return snapshot.Error is { } error
                    ? OperationOutcome<T>.Failed(error)
                    : OperationOutcome<T>.Ok(snapshot.Result!);
            }

            if (elapsed >= deadline)
            {
                // The message must itself say "timed out" - not just carry
                // the OPERATION_POLL_TIMEOUT code - because CloudErrorClassifier
                // falls back to a substring match on the message whenever a
                // structured code it does not specifically recognize is
                // given (this one is poller-local, not a Google code), and
                // a poll deadline expiring is meant to classify the same
                // way a real HttpRequestException/timeout would
                // (docs/cloud_design.md section 5's table).
                return OperationOutcome<T>.Failed(new CloudError(
                    "OPERATION_POLL_TIMEOUT",
                    null,
                    $"Polling timed out after {deadline} without the operation reporting done."));
            }

            var thisWait = backoff;
            if (elapsed + thisWait > deadline)
            {
                thisWait = deadline - elapsed;
            }

            await wait(thisWait, cancellationToken).ConfigureAwait(false);
            elapsed += thisWait;
            backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
        }
    }
}

/// <summary>One poll of an in-flight operation: not done yet, done with a result, or done with an error.</summary>
public sealed record OperationPoll<T>(bool IsDone, T? Result, CloudError? Error);

/// <summary>The final outcome <see cref="OperationPoller.PollAsync{T}"/> resolves to.</summary>
public sealed class OperationOutcome<T>
{
    private OperationOutcome(bool success, T? value, CloudError? error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    public bool Success { get; }

    public T? Value { get; }

    public CloudError? Error { get; }

    public static OperationOutcome<T> Ok(T value) => new(true, value, null);

    public static OperationOutcome<T> Failed(CloudError error) => new(false, default, error);
}
