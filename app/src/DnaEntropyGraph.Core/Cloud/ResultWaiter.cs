using System.Globalization;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The platform's own time at one look (<see cref="VmDescriptor.ServerNow"/>) with a monotonic mark taken when it was read, so "platform now" later is
/// <c>ServerNow + monotonic elapsed</c>. A wall-clock difference stored once and added to a later wall clock is broken by an NTP step or a resume from
/// sleep between the two; a monotonic interval is not (issue #691).
/// </summary>
internal readonly record struct PlatformReading(DateTimeOffset ServerNow, long Mark);

/// <summary>
/// When a VM's platform run-limit clock started (platform time) and the platform reading to age it with; a null reading (no server time, or an
/// implausible one) means "judge on this laptop's own clock".
/// </summary>
internal readonly record struct VmClockBasis(DateTimeOffset RunClockStart, PlatformReading? Reading);

internal static class PlatformClock
{
    /// <summary>
    /// A server time more than this far from the laptop's wall clock is a broken proxy or a broken clock (1970, or days off), not information:
    /// it is refused and the laptop's own clock is used. A real laptop clock is off by seconds, rarely by hours.
    /// </summary>
    public static readonly TimeSpan MaxPlausibleSkew = TimeSpan.FromDays(1);

    /// <summary>
    /// The reading to keep, or null when <paramref name="serverNow"/> is absent or implausible. PRECISION (issue #691 review): the HTTP <c>Date</c>
    /// header has one-second resolution (truncated, so it reads up to 1 s behind the platform) and was generated before the answer crossed the
    /// network (another 0.1 to 1.5 s behind, THEORY (unverified): ToTest measures it). So "platform now" runs at most about 2.5 s BEHIND true, never
    /// ahead: a VM that vanished within that margin of its limit may read vm_unhealthy, and nothing reads run_time_limit early. The margin is small
    /// beside a limit of tens of minutes, so it is documented, not compensated.
    /// </summary>
    public static PlatformReading? Read(DateTimeOffset? serverNow, DateTimeOffset localNow, long mark)
        => serverNow is { } server && (server - localNow).Duration() <= MaxPlausibleSkew ? new PlatformReading(server, mark) : null;
}
internal enum BootOutcome
{
    Running,
    TimedOut,
    Lost,
}

/// <summary>
/// Waiting on the VM and the worker: the boot wait, the <c>result.json</c> poll (with the heartbeat watch, #498), and the judgement of a VM that is lost
/// (including the worker's own error code from <c>status.json</c>). Every way out but success ends the run through
/// <see cref="RunFailureException"/>, with the VM ended first and the outcome of that recorded (Hard Rule 11).
/// </summary>
internal sealed class ResultWaiter(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings, VmTerminator terminator)
{
    /// <summary>How long before the VM's own <c>maxRunDuration</c> the default result timeout ends.</summary>
    private static readonly TimeSpan ResultMargin = TimeSpan.FromMinutes(3);

    /// <summary>A heartbeat unchanged for this many times <c>limits.heartbeatSeconds</c> is dead: 600 s at the 30 s default (docs/job_contract.md section 5, "older than 600 s regardless of instance state").</summary>
    private const int HeartbeatStaleFactor = 20;

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
        var clockStart = settings.StartClock();
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

            if (settings.ElapsedSince(clockStart) >= settings.BootTimeout)
            {
                return (BootOutcome.TimedOut, $"The VM did not finish starting within {settings.BootTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.{(lastTransient is null ? string.Empty : " The last look failed: " + lastTransient + ".")}");
            }

            await settings.DelayAsync(settings.ResultPollInterval, cancellationToken).ConfigureAwait(false);
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
    public async Task<string> AwaitResultAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken, long? vmStartMark = null)
    {
        // Issue #675: a stall question opened to the caller is withdrawn however the wait ends (the result arrived, the run was ended, the run was cancelled).
        using var stall = new StallState();
        return await AwaitResultCoreAsync(request, bucket, zone, cancellationToken, vmStartMark, stall).ConfigureAwait(false);
    }

    /// <summary>The question put to the caller when the heartbeat watch fires (issue #675): the open task, its token source, and the verdict it was asked about.</summary>
    private sealed class StallState : IDisposable
    {
        private CancellationTokenSource? _cts;

        public Task<StallChoice>? Choice { get; private set; }

        public HeartbeatVerdict? Verdict { get; private set; }

        public bool IsOpen => Choice is not null;

        public void Open(StallDecision ask, StallPrompt prompt, HeartbeatVerdict verdict, CancellationToken runToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            Verdict = verdict;
            try
            {
                Choice = ask(prompt, _cts.Token);
            }
            catch (Exception ex)
            {
                // A handler that throws before it returns a task is the same as one whose task faults: no answer.
                Choice = Task.FromException<StallChoice>(ex);
            }
        }

        /// <summary>What the caller chose, or null when its task faulted or was cancelled (no answer).</summary>
        public StallChoice? TakeChoice() => Choice is { IsCompletedSuccessfully: true } done ? done.Result : null;

        /// <summary>Withdraws the question: its token is cancelled and whatever the task later does is ignored.</summary>
        public void Close()
        {
            if (Choice is { } task)
            {
                _cts?.Cancel();
                _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            _cts?.Dispose();
            _cts = null;
            Choice = null;
            Verdict = null;
        }

        public void Dispose() => Close();
    }

    private void Notify(RunNotice notice)
    {
        try
        {
            settings.OnNotice?.Invoke(notice);
        }
        catch (Exception)
        {
            // Whoever shows the notice failing must never end the run.
        }
    }

    private async Task<string> AwaitResultCoreAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken, long? vmStartMark, StallState stall)
    {
        var key = WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json";
        var limit = settings.ResultTimeout ?? ResultWaitLimit(request.Spec.MaxRunDuration);
        var clockStart = settings.StartClock();
        string? lastTransient = null;
        DateTimeOffset? vmCreatedAt = null; // when the platform's limit clock last started, on the PLATFORM's clock (see VmDescriptor.RunClockStart)
        PlatformReading? platform = null; // the platform's time at the last look that found the VM, with a monotonic mark (issue #691); none until one does

        // The VM's maxRunDuration started when Compute Engine last STARTED it (its creation, for a new VM; the start of the reuse, for a VM
        // created days ago, #94), not when it reached Running: a boot of several minutes, or a resume, has already used part of it. With the
        // default limit the clock is moved forward by the VM's age at the first look that reports a start time (a gateway that reports
        // none leaves it at "from Running").
        //
        // Skew-safe: when this run started the VM itself, the age is the runner's OWN clock between that moment and now. Subtracting the
        // platform's timestamp from the app's wall clock instead is wrong by the whole skew of the laptop's clock (an hour fast fires the
        // deadline an hour early). Only a VM the run did not start (adopted after a restart) has no mark and falls back to the wall-clock
        // difference, clamped to the VM's own limit; THEORY (unverified): that fallback is off by the laptop's skew.
        var elapsedBeforeClock = TimeSpan.Zero;
        var ageKnown = settings.ResultTimeout is not null;
        if (vmStartMark is { } mark && !ageKnown)
        {
            ageKnown = true;
            var sinceStart = settings.TimeProvider.GetElapsedTime(mark, clockStart);
            elapsedBeforeClock = sinceStart > TimeSpan.Zero ? sinceStart : TimeSpan.Zero;
        }

        var statusKey = WorkerManifestBuilder.JobPrefix(request.JobId) + "status.json";
        var progressKey = WorkerManifestBuilder.JobPrefix(request.JobId) + "progress.jsonl";
        var watch = new HeartbeatWatch(
            VmProvisioner.ExpectsGpu(request.Spec.MachineType),
            settings.FirstHeartbeatTimeout,
            settings.HeartbeatStaleTimeout ?? TimeSpan.FromSeconds(WorkerManifestBuilder.HeartbeatSeconds * HeartbeatStaleFactor),
            settings.StageDeadlines);

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
                vmCreatedAt = vm?.RunClockStart ?? vmCreatedAt;
                platform = PlatformClock.Read(vm?.ServerNow, settings.TimeProvider.GetUtcNow(), settings.StartClock()) ?? platform;
                if (!ageKnown && vm?.RunClockStart is { } clockBegan)
                {
                    ageKnown = true;
                    var age = PlatformNow(platform) - clockBegan - settings.ElapsedSince(clockStart);
                    elapsedBeforeClock = age > TimeSpan.Zero ? (age < request.Spec.MaxRunDuration ? age : request.Spec.MaxRunDuration) : TimeSpan.Zero;
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

                    // Only a VM that is GONE (not merely stopped or preempted) can be one the platform deleted at its limit.
                    throw await VmLostAsync(request, bucket, zone, reason, cancellationToken, vm is null && vmCreatedAt is { } clockStartedAt ? new VmClockBasis(clockStartedAt, platform) : null).ConfigureAwait(false);
                }

                // RUNNING is not working: the VM being up says nothing about the job (CLAUDE.md Critical Pitfalls). The worker's
                // heartbeat in status.json is the health signal; a dead or frozen worker process ends the run here, not at the result limit.
                var statusText = await calls.TryReadTextAsync(bucket, statusKey, cancellationToken).ConfigureAwait(false);
                var progressText = watch.NeedsProgress ? await calls.TryReadTextAsync(bucket, progressKey, cancellationToken).ConfigureAwait(false) : null;
                var verdict = watch.Observe(statusText, progressText, settings.ElapsedSince(clockStart));
                if (watch.TakeSlowNotice() is { } slow)
                {
                    Notify(new RunNotice(request.JobId, RunNoticeKind.SlowStage, slow.Stage, slow.InStage, slow.Deadline));
                }

                // Issue #675: with a stall handler the verdict is a question for the person (Stop, Delete, Keep waiting), asked once while it is open; the polling for
                // result.json goes on meanwhile. A box with no GPU is never asked about (it is deleted at once, as startup.sh does).
                var forceStop = false;
                var forceDelete = false;
                if (stall.IsOpen)
                {
                    if (stall.Choice!.IsCompleted)
                    {
                        var asked = stall.Verdict!;
                        var choice = stall.TakeChoice();
                        stall.Close();
                        switch (choice)
                        {
                            case StallChoice.KeepWaiting:
                                watch.Snooze(settings.ElapsedSince(clockStart));
                                verdict = null;
                                break;
                            case StallChoice.Stop:
                                verdict = asked;
                                forceStop = true;
                                break;
                            case StallChoice.Delete:
                                verdict = asked;
                                forceDelete = true;
                                break;
                            default:
                                verdict = asked; // no answer: the default end, never a VM left billing
                                break;
                        }
                    }
                    else if (verdict is null)
                    {
                        stall.Close();
                        Notify(new RunNotice(request.JobId, RunNoticeKind.StallCleared));
                    }
                    else
                    {
                        verdict = null; // the question is already open: not repeated
                    }
                }
                else if (verdict is not null && !verdict.DeleteVm && settings.OnStall is { } ask)
                {
                    var waited = settings.ElapsedSince(clockStart);
                    stall.Open(ask, new StallPrompt(request.JobId, verdict.Code, verdict.Message, verdict.Stage, waited), verdict, cancellationToken);
                    Notify(new RunNotice(request.JobId, RunNoticeKind.StallDetected, verdict.Stage, Code: verdict.Code));
                    verdict = null;
                }

                if (verdict is not null)
                {
                    // A worker writes result.json BEFORE it stops its heartbeat: one last look before calling it dead.
                    text = await calls.TryReadTextAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                    if (text is not null)
                    {
                        return text;
                    }

                    // A box with no GPU is deleted whatever the user chose (as startup.sh does); a frozen worker's VM is ended per the user's choice.
                    var heartbeatNote = await terminator.EndVmAfterFailureAsync(request, zone, forceDelete: verdict.DeleteVm || forceDelete, forceStop: forceStop).ConfigureAwait(false);
                    throw VmEndNotes.Apply(new RunFailureException(CloudErrorKind.Other, verdict.Code, verdict.Message), heartbeatNote);
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

            if (settings.ElapsedSince(clockStart) + elapsedBeforeClock >= limit)
            {
                var note = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
                var minutes = limit.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture);
                var last = lastTransient is null ? string.Empty : $" The last look failed: {lastTransient}.";
                throw VmEndNotes.Apply(new RunFailureException(CloudErrorKind.Other, RunErrorCodes.ResultTimeout, $"No result.json after {minutes} minutes.{last}"), note);
            }

            await settings.DelayAsync(settings.ResultPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The platform's time now: the last reading aged by monotonic elapsed time, or this laptop's wall clock when no plausible reading was ever taken.</summary>
    private DateTimeOffset PlatformNow(PlatformReading? reading)
        => reading is { } r ? r.ServerNow + settings.ElapsedSince(r.Mark) : settings.TimeProvider.GetUtcNow();

    /// <summary>
    /// The VM is gone or no longer running and no result.json exists. Boot-time failures
    /// (<c>worker/vm/startup.sh</c>: GPU_NOT_VISIBLE, IMAGE_PULL_FAILED, MANIFEST_INVALID, WORKER_CRASH) write
    /// <c>status.json</c> and end the VM without ever writing result.json, so the cause is read from there
    /// rather than reported as a lost computer.
    /// </summary>
    public async Task<Exception> VmLostAsync(CloudJobRequest request, string bucket, string zone, string? reason, CancellationToken cancellationToken, VmClockBasis? clock = null)
    {
        var workerCode = await TryReadWorkerErrorCodeAsync(bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "status.json", cancellationToken).ConfigureAwait(false);
        var note = await terminator.EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
        var mapped = RunErrorCodes.ForWorkerStatusCode(workerCode);

        // #89: a VM that was seen created, is gone, and whose maxRunDuration has now passed was deleted by Compute Engine at its limit
        // (Hard Rule 10's backstop), not lost. Issue #691: "now passed" is judged on the platform's clock (the last server time read plus the monotonic time since), because the laptop's clock may be an hour out; the platform's time is aged with a MONOTONIC interval so a wall-clock step cannot move it; with no plausible server time the laptop's clock is used. THEORY (unverified): the platform deletes at created + maxRunDuration on a real instance.
        var pastLimit = clock is { } basis && PlatformNow(basis.Reading) >= basis.RunClockStart + request.Spec.MaxRunDuration;
        return VmEndNotes.Apply(
            mapped is not null
                ? new RunFailureException(CloudErrorKind.Other, mapped, $"The worker reported {workerCode} in status.json and no result.json followed. {reason}")
                : pastLimit
                    ? new RunFailureException(CloudErrorKind.Other, RunErrorCodes.RunTimeLimit, $"The VM was gone after its maxRunDuration of {request.Spec.MaxRunDuration.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes and no result.json followed. {reason}")
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
