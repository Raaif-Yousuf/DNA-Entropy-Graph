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

    /// <summary>
    /// Starts <paramref name="body"/> on a background task registered under <paramref name="jobId"/>, and returns a task that ends when the
    /// body has ended and the entry is removed; null when the job already has a driver. The job is registered before the body runs, so a
    /// cancel can never miss it. A cancel the caller asked for ends the task quietly; any other exception is the body's to handle.
    /// </summary>
    public Task? TryStart(string jobId, Func<CancellationToken, Task> body)
    {
        var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_runs.TryAdd(jobId, new Entry(cts, done.Task)))
        {
            cts.Dispose();
            return null;
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

    /// <summary>Cancels the job's driver and waits for it to end. False when the job has none (the caller then cancels the run itself).</summary>
    public async Task<bool> CancelAndWaitAsync(string jobId)
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
