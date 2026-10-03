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
/// <paramref name="fixedInterval"/> replaces the doubling backoff with one constant wait, for an API whose documented polling cadence is fixed (Service Usage: every 5 s, issue #52).
///
/// No real time is ever awaited unless the caller's own <paramref
/// name="delay"/> (default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>)
/// does so - a test supplies an instant, recording delay function so the
/// whole backoff/deadline algorithm is exercised in milliseconds.
/// </summary>
public static class OperationPoller
{
    /// <summary>The <see cref="CloudError.Code"/> of a deadline spent before the operation reported done.</summary>
    public const string TimeoutCode = "OPERATION_POLL_TIMEOUT";

    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    /// <param name="poll">One read of the operation. It receives a token that ends when the caller cancels OR the deadline is spent, so a read that hangs ends at the deadline instead of at the HTTP client's own (longer) timeout.</param>
    /// <param name="deadline">One wall-clock budget for the whole poll, including the time spent inside each read.</param>
    /// <param name="time">The clock the deadline runs on; null is the system clock. A test passes its own.</param>
    public static async Task<OperationOutcome<T>> PollAsync<T>(
        Func<CancellationToken, Task<OperationPoll<T>>> poll,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? time = null,
        TimeSpan? fixedInterval = null)
    {
        ArgumentNullException.ThrowIfNull(poll);

        var clock = time ?? TimeProvider.System;
        var wait = delay ?? ((span, ct) => Task.Delay(span, clock, ct));
        var backoff = fixedInterval ?? InitialBackoff;
        var waited = TimeSpan.Zero;
        var started = clock.GetTimestamp();
        var firstRead = true;

        // The wall clock is the truth. The waits asked for are budgeted too, so an instant (test) delay function that
        // returns early still spends the deadline instead of polling forever.
        TimeSpan Elapsed()
        {
            var wall = clock.GetElapsedTime(started);
            return wall > waited ? wall : waited;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The first read always happens (a zero deadline still reads once). Every later read gets only what is left;
            // when nothing is left the poll is over, and a read must never be handed a fresh full deadline.
            var remaining = deadline - Elapsed();
            if (!firstRead && remaining <= TimeSpan.Zero)
            {
                return TimedOut<T>(deadline);
            }

            firstRead = false;
            using var deadlineSource = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineSource.Token);

            OperationPoll<T> snapshot;
            try
            {
                snapshot = await poll(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadlineSource.IsCancellationRequested)
            {
                return TimedOut<T>(deadline);
            }

            if (snapshot.IsDone)
            {
                return snapshot.Error is { } error
                    ? OperationOutcome<T>.Failed(error)
                    : OperationOutcome<T>.Ok(snapshot.Result!);
            }

            var left = deadline - Elapsed();
            if (left <= TimeSpan.Zero)
            {
                return TimedOut<T>(deadline);
            }

            var thisWait = backoff < left ? backoff : left;
            await wait(thisWait, cancellationToken).ConfigureAwait(false);
            waited += thisWait;
            if (fixedInterval is null)
            {
                backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
            }
        }
    }

    // The message must itself say "timed out" - not just carry the OPERATION_POLL_TIMEOUT code - because
    // CloudErrorClassifier falls back to a substring match on the message whenever a structured code it does not
    // specifically recognize is given (this one is poller-local, not a Google code), and a poll deadline expiring is
    // meant to classify the same way a real HttpRequestException/timeout would (docs/cloud_design.md section 5's table).
    private static OperationOutcome<T> TimedOut<T>(TimeSpan deadline)
        => OperationOutcome<T>.Failed(new CloudError(TimeoutCode, null, $"Polling timed out after {deadline} without the operation reporting done."));
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
