using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>How far a graceful cancel got (issue #675): what the caller may infer about the run's partial outputs.</summary>
internal enum GracefulOutcome
{
    /// <summary>No request was written (nothing running, no worker, the step is off, or the request could not be written).</summary>
    NotAsked,

    /// <summary>The request was written and the worker never said it saw it (or the caller gave up): its outputs, if any, are only what it uploaded on its own.</summary>
    NoAcknowledgement,

    /// <summary>The worker acknowledged the request (<c>cancelSeen</c>) but did not reach a terminal stage in the finish window.</summary>
    Acknowledged,

    /// <summary>The worker acknowledged, stopped and wrote its terminal state: its partial outputs are uploaded and listed in <c>result.json</c>.</summary>
    Finished,
}

/// <summary>
/// Issue #675, docs/job_contract.md section 6: before a cancel deletes the VM it asks the worker to stop. It writes the 0-byte <c>control/cancel</c> object, then waits up to
/// <see cref="CloudRunSettings.CancelAckTimeout"/> (60 s) for the worker's EXPLICIT acknowledgement (<c>cancelSeen: true</c> in <c>status.json</c>, written the moment the worker
/// sees the object; or a terminal stage or <c>result.json</c>). A heartbeat tick alone is no answer: it only says the worker is alive, not that it read the request. Then up to
/// <see cref="CloudRunSettings.CancelFinishTimeout"/> for it to finish, so the outputs it already produced are uploaded and kept (the worker stops between windows, never mid-window).
/// Nothing here can keep a VM alive: every way out (no worker to ask, a request that cannot be written, a worker that never answers, an answer that never finishes, the caller giving up)
/// returns and the caller deletes the VM. Best effort and silent: a failure to ask is not a failure to cancel.
/// </summary>
internal sealed class GracefulCancel(IComputeGateway compute, IStorageGateway storage, GatewayCalls calls, CloudRunSettings settings)
{
    private static readonly TimeSpan MaxLook = TimeSpan.FromSeconds(2);

    public async Task<GracefulOutcome> AskAsync(string jobId, string bucket, CancellationToken cancellationToken)
    {
        var outcome = GracefulOutcome.NotAsked;
        if (settings.CancelAckTimeout <= TimeSpan.Zero)
        {
            return outcome;
        }

        var prefix = WorkerManifestBuilder.JobPrefix(jobId);
        try
        {
            var vms = await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
            if (!vms.Any(v => v.Status == "RUNNING"))
            {
                return outcome; // nothing is running to answer
            }

            var before = await ReadStatusAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
            if (before is not { WorkerOwned: true } || IsTerminal(before) || await ResultExistsAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
            {
                return outcome; // no worker has started (nobody to answer), or it has already finished
            }

            await calls.CallAsync(
                async token =>
                {
                    using var empty = new MemoryStream();
                    await storage.UploadAsync(bucket, prefix + "control/cancel", empty, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            outcome = GracefulOutcome.NoAcknowledgement;

            var started = settings.StartClock();
            var acknowledged = false;
            while (settings.ElapsedSince(started) < settings.CancelAckTimeout)
            {
                await settings.DelayAsync(Look(settings.CancelAckTimeout), cancellationToken).ConfigureAwait(false);
                var now = await ReadStatusAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
                if (now is not null && (IsTerminal(now) || now.CancelSeen))
                {
                    acknowledged = true;
                    break;
                }

                if (await ResultExistsAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
                {
                    return GracefulOutcome.Finished;
                }
            }

            if (!acknowledged)
            {
                return outcome; // no acknowledgement within the window: the caller deletes the VM
            }

            outcome = GracefulOutcome.Acknowledged;

            started = settings.StartClock();
            while (settings.ElapsedSince(started) < settings.CancelFinishTimeout)
            {
                var now = await ReadStatusAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
                if ((now is not null && IsTerminal(now)) || await ResultExistsAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
                {
                    return GracefulOutcome.Finished;
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

        return outcome;
    }

    private TimeSpan Look(TimeSpan window)
    {
        var look = settings.ResultPollInterval < MaxLook ? settings.ResultPollInterval : MaxLook;
        return look < window ? look : window;
    }

    private static bool IsTerminal(WorkerStatusSnapshot status) => status.Stage is WorkerStages.Done or WorkerStages.Failed or WorkerStages.Cancelled;

    private async Task<WorkerStatusSnapshot?> ReadStatusAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        var text = await calls.TryReadTextAsync(bucket, prefix + "status.json", cancellationToken).ConfigureAwait(false);
        return text is null ? null : WorkerStatusReader.TryParseStatus(text);
    }

    private async Task<bool> ResultExistsAsync(string bucket, string prefix, CancellationToken cancellationToken)
        => await calls.TryReadTextAsync(bucket, prefix + "result.json", cancellationToken).ConfigureAwait(false) is not null;
}
