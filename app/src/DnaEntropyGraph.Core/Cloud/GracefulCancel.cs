using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #675, docs/job_contract.md section 6: before a cancel deletes the VM it asks the worker to stop. It writes the 0-byte <c>control/cancel</c> object, then waits up to
/// <see cref="CloudRunSettings.CancelAckTimeout"/> (60 s) for a sign of life (the heartbeat moves, or the worker reaches a terminal stage), then up to
/// <see cref="CloudRunSettings.CancelFinishTimeout"/> for it to finish, so the outputs it already produced are uploaded and kept (the worker stops between windows, never mid-window).
/// Nothing here can keep a VM alive: every way out (no worker to ask, a request that cannot be written, a worker that never answers, an answer that never finishes, the caller giving up)
/// returns and the caller deletes the VM. Best effort and silent: a failure to ask is not a failure to cancel.
/// </summary>
internal sealed class GracefulCancel(IComputeGateway compute, IStorageGateway storage, GatewayCalls calls, CloudRunSettings settings)
{
    private static readonly TimeSpan MaxLook = TimeSpan.FromSeconds(2);

    public async Task AskAsync(string jobId, string bucket, CancellationToken cancellationToken)
    {
        if (settings.CancelAckTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var prefix = WorkerManifestBuilder.JobPrefix(jobId);
        try
        {
            var vms = await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
            if (!vms.Any(v => v.Status == "RUNNING"))
            {
                return; // nothing is running to answer
            }

            var before = await ReadStatusAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
            if (before is not { WorkerOwned: true } || IsTerminal(before) || await ResultExistsAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
            {
                return; // no worker has started (nobody to answer), or it has already finished
            }

            await calls.CallAsync(
                async token =>
                {
                    using var empty = new MemoryStream();
                    await storage.UploadAsync(bucket, prefix + "control/cancel", empty, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            var started = settings.StartClock();
            var acknowledged = false;
            while (settings.ElapsedSince(started) < settings.CancelAckTimeout)
            {
                await settings.DelayAsync(Look(settings.CancelAckTimeout), cancellationToken).ConfigureAwait(false);
                var now = await ReadStatusAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
                if (now is not null && (IsTerminal(now) || now.HeartbeatSeq != before.HeartbeatSeq || now.UpdatedAt != before.UpdatedAt))
                {
                    acknowledged = true;
                    break;
                }

                if (await ResultExistsAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (!acknowledged)
            {
                return; // no sign of life within the window: the caller deletes the VM
            }

            started = settings.StartClock();
            while (settings.ElapsedSince(started) < settings.CancelFinishTimeout)
            {
                var now = await ReadStatusAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
                if ((now is not null && IsTerminal(now)) || await ResultExistsAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await settings.DelayAsync(Look(settings.CancelFinishTimeout), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (CloudOperationException)
        {
            // A request that cannot be written or a look that fails: the VM is deleted next, as for a worker that never answered.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up on waiting: the delete still happens (VmCanceller).
        }
    }

    private TimeSpan Look(TimeSpan window)
    {
        var look = settings.ResultPollInterval < MaxLook ? settings.ResultPollInterval : MaxLook;
        return look < window ? look : window;
    }

    private static bool IsTerminal(WorkerStatusSnapshot status) => status.Stage is "done" or "failed" or "cancelled";

    private async Task<WorkerStatusSnapshot?> ReadStatusAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        var text = await calls.TryReadTextAsync(bucket, prefix + "status.json", cancellationToken).ConfigureAwait(false);
        return text is null ? null : WorkerStatusReader.TryParseStatus(text);
    }

    private async Task<bool> ResultExistsAsync(string bucket, string prefix, CancellationToken cancellationToken)
        => await calls.TryReadTextAsync(bucket, prefix + "result.json", cancellationToken).ConfigureAwait(false) is not null;
}
