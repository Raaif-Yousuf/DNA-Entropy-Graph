using System.Text;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The one place a gateway call gets its own deadline, shared by every collaborator of <see cref="CloudJobRunner"/>
/// (it was a private method of the runner before the split, #512).
/// </summary>
internal sealed class GatewayCalls(CloudRunSettings settings, IStorageGateway storage)
{
    /// <summary>Largest <c>result.json</c> the app will read; a real one is a few KiB.</summary>
    private const int MaxResultChars = 4 * 1024 * 1024;

    /// <summary>
    /// One gateway call with its own deadline (<see cref="CloudRunSettings.CallTimeout"/>), so a call that never answers cannot
    /// hang the run. A deadline that passes while the caller is still waiting is reported the way the
    /// resilience pipeline reports one: a network failure. The caller cancelling still cancels.
    /// </summary>
    public async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var limit = timeout ?? settings.CallTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        Task<T>? task = null;
        try
        {
            task = call(deadline.Token);

            // WaitAsync, not just a token the callee may ignore: a call that never looks at its token (a stuck
            // socket under a library that does not check) must still be cut at the deadline.
            return await task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            if (task is not null)
            {
                // The abandoned call may still fault later; nobody is waiting to see it.
                _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            throw new CloudOperationException(new CloudError("TIMEOUT", null, "request timed out"), CloudErrorKind.Network);
        }
    }

    /// <summary>Reads a small object as text; null means it is not there. A body past <see cref="MaxResultChars"/> comes back cut at the limit, which no strict reader will accept as a result.</summary>
    public Task<string?> TryReadTextAsync(string bucket, string key, CancellationToken cancellationToken)
        => CallAsync(
            async token =>
            {
                var stream = await storage.TryDownloadAsync(bucket, key, token).ConfigureAwait(false);
                if (stream is null)
                {
                    return null;
                }

                await using (stream.ConfigureAwait(false))
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var buffer = new char[MaxResultChars + 1];
                    var read = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    return new string(buffer, 0, Math.Min(read, MaxResultChars));
                }
            },
            cancellationToken);
}
