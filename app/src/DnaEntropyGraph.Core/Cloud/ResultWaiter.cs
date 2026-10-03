using System.Diagnostics;
using System.Globalization;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

internal enum BootOutcome
{
    Running,
    TimedOut,
    Lost,
}

/// <summary>
/// Waiting on the VM and the worker: the boot wait, the <c>result.json</c> poll, and the judgement of a VM that is lost
/// (including the worker's own error code from <c>status.json</c>). Every way out but success ends the run through
/// <see cref="RunFailureException"/>, with the VM ended first and the outcome of that recorded (Hard Rule 11).
/// </summary>
internal sealed class ResultWaiter(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings, VmTerminator terminator)
{
    /// <summary>How long before the VM's own <c>maxRunDuration</c> the default result timeout ends.</summary>
    private static readonly TimeSpan ResultMargin = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The default wait for result.json, measured from Running, for a VM with this <c>maxRunDuration</c>:
    /// the VM's own limit minus <see cref="ResultMargin"/>. Compute Engine deletes the VM at
    /// <c>maxRunDuration</c> (Hard Rule 10), so a wait that runs past it only ever finds a lost VM and
    /// <c>result_timeout</c> could never be recorded. Stopping a little earlier lets the runner give up on
    /// its own terms: one last look for the result, then end the VM per the user's choice and verify it.
    /// The worker's own limit is the same <c>maxRunDuration</c>, so a worker still uploading in the final
    /// margin loses that tail and the run fails as a timeout, which is the honest outcome.
    /// </summary>
    public static TimeSpan ResultWaitLimit(TimeSpan maxRunDuration)
        => maxRunDuration - TimeSpan.FromTicks(Math.Min(ResultMargin.Ticks, maxRunDuration.Ticks / 2));

    /// <summary>
    /// Waits for a freshly created VM to read RUNNING. THEORY (unverified): a real instance reads PROVISIONING and then
    /// STAGING for a short while right after insert, so those are "still booting", polled until
    /// <see cref="CloudRunSettings.BootTimeout"/>, never "lost". A transient failure of the look is retried the same way; only a failure
    /// of a class that aborts (billing, API off, permission, org policy) leaves here as an exception. A VM that is
    /// already stopped, gone or terminated is <see cref="BootOutcome.Lost"/>: the caller reads result.json and status.json.
    /// </summary>
    public async Task<(BootOutcome Outcome, string? Reason)> WaitForBootAsync(CloudJobRequest request, string zone, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        string? lastTransient = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var vm = await calls.CallAsync(token => compute.GetVmAsync(request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                if (vm is { Status: "RUNNING" })
                {
                    return (BootOutcome.Running, null);
                }

                if (!VmFacts.IsBooting(vm))
                {
                    VmFacts.IsHealthyRunning(vm, out var reason);
                    return (BootOutcome.Lost, reason);
                }

                lastTransient = null;
            }
            catch (CloudOperationException ex) when (!VmFacts.IsProjectWide(ex.Kind))
            {
                lastTransient = ex.Message;
            }

            if (clock.Elapsed >= settings.BootTimeout)
            {
                return (BootOutcome.TimedOut, $"The VM did not finish starting within {settings.BootTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.{(lastTransient is null ? string.Empty : " The last look failed: " + lastTransient + ".")}");
            }

            await Task.Delay(settings.ResultPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls for <c>jobs/&lt;jobId&gt;/result.json</c> and returns its text. A transient failure (the network,
    /// a 5xx, an open breaker, a call that hung past the call timeout) never ends the run: the VM
    /// and the worker carry on without the app, so it keeps looking until the deadline. Only the classes
    /// CLAUDE.md says abort (billing, API off, permission, org policy) end it early. Every way out but
    /// success ends the run through <see cref="RunFailureException"/>, with the VM ended per the user's
    /// choice first and the outcome of that recorded (Hard Rule 11).
    /// </summary>
    public async Task<string> AwaitResultAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken)
    {
        var key = WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json";
        var limit = settings.ResultTimeout ?? ResultWaitLimit(request.Spec.MaxRunDuration);
        var clock = Stopwatch.StartNew();
        string? lastTransient = null;

        // The VM's maxRunDuration started when Compute Engine created it, not when it reached Running: a boot of several
        // minutes, or a resume, has already used part of it. With the default limit the clock is moved forward by the VM's
        // age at the first look that reports a creation time (a gateway that reports none leaves it at "from Running").
        var elapsedBeforeClock = TimeSpan.Zero;
        var ageKnown = settings.ResultTimeout is not null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var text = await calls.TryReadTextAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                if (text is not null)
                {
                    return text;
                }

                var vm = await calls.CallAsync(token => compute.GetVmAsync(request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                lastTransient = null;
                if (!ageKnown && vm?.CreatedAt is { } createdAt)
                {
                    ageKnown = true;
                    var age = settings.TimeProvider.GetUtcNow() - createdAt - clock.Elapsed;
                    elapsedBeforeClock = age > TimeSpan.Zero ? age : TimeSpan.Zero;
                }

                if (!VmFacts.IsHealthyRunning(vm, out var reason) && !VmFacts.IsBooting(vm))
                {
                    // A worker writes result.json BEFORE it stops its own VM, so look once more before
                    // calling a stopped VM lost.
                    text = await calls.TryReadTextAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                    if (text is not null)
                    {
                        return text;
                    }

                    throw await VmLostAsync(request, bucket, zone, reason, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (CloudOperationException ex) when (VmFacts.IsProjectWide(ex.Kind))
            {
                var abortNote = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
                throw VmEndNotes.Apply(ex, abortNote);
            }
            catch (CloudOperationException ex)
            {
                lastTransient = ex.Message;
            }

            if (clock.Elapsed + elapsedBeforeClock >= limit)
            {
                var note = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
                var minutes = limit.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture);
                var last = lastTransient is null ? string.Empty : $" The last look failed: {lastTransient}.";
                throw VmEndNotes.Apply(new RunFailureException(CloudErrorKind.Other, RunErrorCodes.ResultTimeout, $"No result.json after {minutes} minutes.{last}"), note);
            }

            await Task.Delay(settings.ResultPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The VM is gone or no longer running and no result.json exists. Boot-time failures
    /// (<c>worker/vm/startup.sh</c>: GPU_NOT_VISIBLE, IMAGE_PULL_FAILED, MANIFEST_INVALID, WORKER_CRASH) write
    /// <c>status.json</c> and end the VM without ever writing result.json, so the cause is read from there
    /// rather than reported as a lost computer.
    /// </summary>
    public async Task<Exception> VmLostAsync(CloudJobRequest request, string bucket, string zone, string? reason, CancellationToken cancellationToken)
    {
        var workerCode = await TryReadWorkerErrorCodeAsync(bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "status.json", cancellationToken).ConfigureAwait(false);
        var note = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
        var mapped = RunErrorCodes.ForWorkerStatusCode(workerCode);
        return VmEndNotes.Apply(
            mapped is not null
                ? new RunFailureException(CloudErrorKind.Other, mapped, $"The worker reported {workerCode} in status.json and no result.json followed. {reason}")
                : new RunFailureException(CloudErrorKind.Other, RunErrorCodes.VmUnhealthy, (reason ?? "The VM is no longer running.") + (workerCode is null ? string.Empty : $" status.json reported {workerCode}.")),
            note);
    }

    /// <summary>The <c>error.code</c> in <c>status.json</c>, or null when it is absent, unreadable or the look itself failed (a diagnostic, never worth failing the run over).</summary>
    private async Task<string?> TryReadWorkerErrorCodeAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var text = await calls.TryReadTextAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return text is null ? null : WorkerResultReader.TryReadStatusErrorCode(text);
        }
        catch (CloudOperationException)
        {
            return null;
        }
    }
}
