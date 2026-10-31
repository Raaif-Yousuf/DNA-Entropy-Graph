namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #588 (cold review): one result file streamed from the bucket into a temp file, under a STALL deadline (no bytes for
/// <c>stall</c> cancels the attempt) plus a generous total cap derived from the size, so a connected but silent socket cannot hang a run
/// forever and a slow but live link is not cut. Both consumers (the run's download and the History re-download) use it.
/// On a timeout the inner token is cancelled and the callee is awaited (bounded by <see cref="CancelGrace"/>) before the caller deletes the temp file.
/// </summary>
internal static class StreamedDownload
{
    public static readonly TimeSpan DefaultStall = TimeSpan.FromSeconds(90);

    /// <summary>How long a cancelled attempt is given to let go of its file before it is abandoned.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(30);

    private const double MinBytesPerSecond = 256 * 1024;

    /// <summary>The whole-download cap: the time the file takes at 256 KiB/s (floor 5 minutes), or 30 minutes when the size is unknown.</summary>
    public static TimeSpan TotalCap(long? bytes)
        => bytes is { } b ? TimeSpan.FromSeconds(Math.Max(300, b / MinBytesPerSecond)) : TimeSpan.FromMinutes(30);

    /// <summary>A unique temp name per attempt, so two downloads of one file (a re-download beside a run) never write the same file.</summary>
    public static string TempPathFor(string destination) => destination + "." + Guid.NewGuid().ToString("N")[..12] + ".part";

    /// <summary>The file's current length, or -1 when it does not exist yet. May throw <see cref="IOException"/> if the file vanishes or is locked mid-read.</summary>
    private static long ProbeLength(string path) => File.Exists(path) ? new FileInfo(path).Length : -1;

    public static async Task<bool> RunAsync(IStorageGateway storage, TimeProvider clock, CloudScope scope, string bucket, string objectKey, string tempPath, long? expectedBytes, TimeSpan stall, CancellationToken cancellationToken, Func<string, long>? lengthProbe = null, TimeSpan? cancelGrace = null)
    {
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cap = TotalCap(expectedBytes);
        var started = clock.GetTimestamp();
        var download = Task.Run(() => storage.TryDownloadToFileAsync(scope, bucket, objectKey, tempPath, inner.Token), CancellationToken.None);

        var watcher = Task.Run(
            async () =>
            {
                var poll = TimeSpan.FromMilliseconds(Math.Clamp(stall.TotalMilliseconds / 4, 10, 5000));
                long lastLength = -1;
                var lastProgress = clock.GetTimestamp();
                try
                {
                    while (true)
                    {
                        await Task.Delay(poll, clock, inner.Token).ConfigureAwait(false);
                        long length;
                        try
                        {
                            length = (lengthProbe ?? ProbeLength)(tempPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // The file vanished or is locked between the check and the read: that is "no progress observed yet", never a stall by itself
                            // (a probe that keeps failing still ends in the stall deadline below, because lastProgress does not move).
                            length = lastLength;
                        }

                        if (length != lastLength)
                        {
                            lastLength = length;
                            lastProgress = clock.GetTimestamp();
                        }

                        if (clock.GetElapsedTime(lastProgress) >= stall || clock.GetElapsedTime(started) >= cap)
                        {
                            await inner.CancelAsync().ConfigureAwait(false);
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            },
            CancellationToken.None);

        var first = await Task.WhenAny(download, watcher).ConfigureAwait(false);
        if (first == download)
        {
            await inner.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
            return await download.ConfigureAwait(false);
        }

        // The watcher fired: wait (bounded) for the callee to let go of the file, so the caller's delete cannot race a live writer.
        await Task.WhenAny(download, Task.Delay(cancelGrace ?? CancelGrace, clock)).ConfigureAwait(false);
        if (download.IsCompleted)
        {
            _ = download.Exception;
        }
        else
        {
            // The callee ignored the cancellation and may still hold (or recreate) the temp file, so the caller's delete can fail. Once it does let go, observe its
            // exception and delete the file ourselves, instead of leaving it until the next day's sweep.
            _ = download.ContinueWith(
                finished =>
                {
                    _ = finished.Exception;
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The next sweep takes it.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw new CloudOperationException(new CloudError("TIMEOUT", null, "request timed out"), CloudErrorKind.Network);
    }
}
