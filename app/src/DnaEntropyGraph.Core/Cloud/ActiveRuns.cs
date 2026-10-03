using System.Collections.Concurrent;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The one owner of "the task driving this job" (issue #59). The engine registers a run it starts here and the reconciler
/// registers a run it reattaches, so a cancel always finds the driver, stops it and waits for it BEFORE it writes its own
/// phase: one writer on a run's row at a time.
/// </summary>
public sealed class ActiveRuns
{
    private readonly ConcurrentDictionary<string, Entry> _runs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancels = new();

    /// <summary>
    /// Raised with the job id once a driver is registered for it (<see cref="IsActive"/> is true) and before its body runs; not raised for a start
    /// that was refused. A signal to wait on instead of polling <see cref="IsActive"/>. A handler that throws is ignored: it never stops the driver.
    /// </summary>
    public event Action<string>? Started;

    /// <summary>
    /// Starts <paramref name="body"/> on a background task registered under <paramref name="jobId"/>, and returns a task that ends when the
    /// body has ended and the entry is removed; null when the job already has a driver or a user cancel (<see cref="CancelAsync"/>) is running for it. The job is registered before the body runs, so a
    /// cancel can never miss it. A cancel the caller asked for ends the task quietly; any other exception is the body's to handle.
    /// </summary>
    public Task? TryStart(string jobId, Func<CancellationToken, Task> body)
    {
        var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(cts, done.Task);
        if (!_runs.TryAdd(jobId, entry))
        {
            cts.Dispose();
            return null;
        }

        // A user cancel owns the job until it ends, even after the reattach stopped waiting for it (its settle deadline): a second driver here
        // could resume a run whose VM the cancel is deleting (issue #551). Checked AFTER the registration, and the cancel registers its marker
        // BEFORE it stops the driver, so a cancel that begins after this check still finds and stops this entry, and one that began before refuses it.
        if (_cancels.ContainsKey(jobId))
        {
            _runs.TryRemove(new KeyValuePair<string, Entry>(jobId, entry));
            done.TrySetResult(); // a cancel that already found this entry is waiting on it
            cts.Dispose();
            return null;
        }

        // One handler at a time, each guarded: a listener's failure is its own, and neither stops the driver nor starves the listeners after it.
        foreach (var handler in Started?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<string>)handler)(jobId);
            }
            catch (Exception)
            {
                // Ignored on purpose (see above).
            }
        }

        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await body(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // CancelAndWaitAsync's caller owns the Cancelling/Cancelled records.
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Removed BEFORE the returned task completes, so whoever awaits it sees the job as no longer driven.
            _runs.TryRemove(jobId, out _);
            cts.Dispose();
            if (failure is null)
            {
                done.TrySetResult();
            }
            else
            {
                done.TrySetException(failure);
            }
        });
        return done.Task;
    }

    public bool IsActive(string jobId) => _runs.ContainsKey(jobId);

    /// <summary>
    /// A user cancel (one at a time per job; a second concurrent call waits for the first and runs nothing): stops the job's driver (if any) and waits for it to end, then runs <paramref name="cancel"/>, the code that writes the
    /// cancel's own phases. Until <paramref name="cancel"/> has ended, successfully or not, <see cref="WhenCancelSettledAsync"/> for the job does
    /// not complete, so whoever was driving the run can wait for the cancel without polling and without waiting on one that already failed.
    /// An exception from <paramref name="cancel"/> reaches the caller.
    /// </summary>
    public async Task CancelAsync(string jobId, Func<Task> cancel)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_cancels.TryAdd(jobId, settled))
        {
            // A second cancel (a double click) writes nothing: the first owns the run's phases, and this one returns when it has ended.
            await WhenCancelSettledAsync(jobId).ConfigureAwait(false);
            return;
        }

        try
        {
            // The marker is in place BEFORE the driver is stopped, so a driver that sees itself stopped always finds it.
            await StopDriverAsync(jobId).ConfigureAwait(false);
            await cancel().ConfigureAwait(false);
        }
        finally
        {
            _cancels.TryRemove(jobId, out _);
            settled.TrySetResult();
        }
    }

    /// <summary>Completes when the user cancel in progress for the job (<see cref="CancelAsync"/>) has ended; already complete when there is none.</summary>
    public Task WhenCancelSettledAsync(string jobId) => _cancels.TryGetValue(jobId, out var settled) ? settled.Task : Task.CompletedTask;

    /// <summary>Stops the job's driver and waits for it to end. False when the job has none. Private: only <see cref="CancelAsync"/> may stop a driver, so the settle marker is always in place.</summary>
    private async Task<bool> StopDriverAsync(string jobId)
    {
        if (!_runs.TryGetValue(jobId, out var entry))
        {
            return false;
        }

        try
        {
            await entry.Cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The run finished and disposed its source between the lookup and here.
        }

        try
        {
            await entry.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The driver's own failure is its starter's to see; the cancel only needs it to have stopped.
        }

        return true;
    }

    private sealed record Entry(CancellationTokenSource Cts, Task Task);
}
