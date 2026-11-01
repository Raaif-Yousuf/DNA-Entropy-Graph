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
internal sealed class ResultWaiter(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings, VmTerminator terminator, Func<string, DateTimeOffset, CancellationToken, Task>? recordHeartbeat = null)
{
    /// <summary>The most often a fresh worker heartbeat is written to the run row (issue #109): the row only needs "alive until about here", not every poll.</summary>
    private static readonly TimeSpan HeartbeatRecordInterval = TimeSpan.FromMinutes(1);

    /// <summary>Writes the heartbeat the run just saw to its row, at most once per <see cref="HeartbeatRecordInterval"/> (monotonic). A record, not a step: a failure never ends the run.</summary>
    private async Task<long?> RecordHeartbeatAsync(string jobId, DateTimeOffset? heartbeatAt, long? lastMark, CancellationToken cancellationToken)
    {
        if (heartbeatAt is not { } reported || recordHeartbeat is null || (lastMark is { } mark && settings.ElapsedSince(mark) < HeartbeatRecordInterval))
        {
            return lastMark;
        }

        // The worker's clock can run ahead of the app's. A heartbeat "from the future" would be stored, refuse every later real one (only newer replaces) and be booked as the VM's end:
        // the row holds the earlier of the worker's time and the app's own now.
        var now = settings.TimeProvider.GetUtcNow();
        var at = reported < now ? reported : now;

        var written = settings.StartClock();
        try
        {
            await recordHeartbeat(jobId, at, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The row keeps its last heartbeat; the run goes on. A cancelled run is the one thing that is not swallowed.
        }

        return written;
    }

    /// <summary>The newest incarnation (instance id, creation time) of each job's VM this waiter has looked at alive: what a preemption lookup is bound to (#91).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, VmIncarnation> _seenIncarnations = new(StringComparer.Ordinal);

    private void Observe(string jobId, VmDescriptor? vm)
    {
        if (vm is not null && (vm.InstanceId is not null || vm.CreatedAt is not null))
        {
            _seenIncarnations[jobId] = new VmIncarnation(vm.InstanceId, vm.CreatedAt);
        }
    }

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
    public async Task<(BootOutcome Outcome, string? Reason)> WaitForBootAsync(CloudJobRequest request, string zone, bool adopted, CancellationToken cancellationToken)
    {
        var clockStart = settings.StartClock();
        var waitStart = clockStart;
        string? lastTransient = null;
        var granted = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var vm = await calls.CallAsync(token => compute.GetVmAsync(request.ResolvedScope, request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                Observe(request.JobId, vm);
                if (vm is { Status: "RUNNING" })
                {
                    return (BootOutcome.Running, null);
                }

                // Issue #92 (round 2 H1): a flex request adopted after the app was closed can still be queued (PROVISIONING). That is waiting by choice, not booting:
                // BootTimeout starts only once the request is no longer queued, and the wait is bounded by the request's own window instead (its age on the platform's
                // clock when it reports one, else from this look). THEORY (unverified, no live project): a queued flex instance reads PROVISIONING until it is granted.
                // Round 3 (F2 iii): only for a request ADOPTED from an earlier run. One this run created and was granted that then hangs booting keeps the
                // boot hang detector (BootTimeout), as any other VM does.
                if (vm is not null && vm.Status != "PROVISIONING")
                {
                    granted = true;
                }

                if (adopted && request.Spec.FlexStart && vm is { Status: "PROVISIONING" } queued && QueuedWindowOpen(request.Spec, queued, waitStart))
                {
                    clockStart = settings.StartClock();
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

                // Round 3 (F2 iv): a failed look cannot tell a queued adopted request from a dead one, so while the request may still be queued (never seen granted,
                // inside its own window) it does not count toward BootTimeout. After the window the ordinary boot timeout applies.
                if (adopted && request.Spec.FlexStart && !granted && QueuedWindowOpen(request.Spec, null, waitStart))
                {
                    clockStart = settings.StartClock();
                }
            }

            if (settings.ElapsedSince(clockStart) >= settings.BootTimeout)
            {
                return (BootOutcome.TimedOut, $"The VM did not finish starting within {settings.BootTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.{(lastTransient is null ? string.Empty : " The last look failed: " + lastTransient + ".")}");
            }

            await settings.DelayAsync(settings.ResultPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True while a queued flex request is inside the window it asked Compute to wait for capacity (the spec's wait plus its margin).</summary>
    private bool QueuedWindowOpen(VmSpec spec, VmDescriptor? vm, long waitStart)
    {
        var bound = spec.FlexStartCreateBound;

        // Round 3 (F2 ii): the platform's CreatedAt is only ever compared with the PLATFORM's own clock (ServerNow); with no server time the window runs from this
        // wait's start (the adoption) on the app's monotonic clock, never the app's wall clock minus a platform timestamp. The platform expires the request by itself
        // (the VM then vanishes and the wait ends as a lost VM), so this window is only a backstop.
        if (vm is { CreatedAt: { } created, ServerNow: { } now })
        {
            return now - created < bound;
        }

        return settings.ElapsedSince(waitStart) < bound;
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
        try
        {
            return await AwaitResultCoreAsync(request, bucket, zone, cancellationToken, vmStartMark, stall).ConfigureAwait(false);
        }
        finally
        {
            // Notices run off the poll loop (a blocking handler must not hang it); the ones already given are let finish, but never for longer than a moment.
            await stall.DrainNoticesAsync(NoticeDrainTimeout).ConfigureAwait(false);
        }
    }

    /// <summary>The question put to the caller when the heartbeat watch fires (issue #675): the open task, its token source, and the verdict it was asked about.</summary>
    private sealed class StallState : IDisposable
    {
        private readonly object _noticeGate = new();
        private Task _notices = Task.CompletedTask;
        private CancellationTokenSource? _cts;

        /// <summary>Runs <paramref name="handler"/> with the notice on the thread pool, after the notices given before it (in order), never on the caller's thread.</summary>
        public void Enqueue(Action<RunNotice> handler, RunNotice notice)
        {
            lock (_noticeGate)
            {
                _notices = _notices.ContinueWith(
                    _ =>
                    {
                        try
                        {
                            handler(notice);
                        }
                        catch (Exception)
                        {
                            // Whoever shows the notice failing must never end the run.
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        public async Task DrainNoticesAsync(TimeSpan limit)
        {
            Task pending;
            lock (_noticeGate)
            {
                pending = _notices;
            }

            await Task.WhenAny(pending, Task.Delay(limit, TimeProvider.System)).ConfigureAwait(false);
        }

        public Task<StallChoice>? Choice { get; private set; }

        public HeartbeatVerdict? Verdict { get; private set; }

        public bool IsOpen => Choice is not null;

        public void Open(StallDecision ask, StallPrompt prompt, HeartbeatVerdict verdict, CancellationToken runToken, TimeSpan? answerTimeout, TimeProvider time)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            Verdict = verdict;
            var token = _cts.Token;

            // The handler runs on the thread pool, never on the poll loop: one that blocks synchronously (a modal dialog on the wrong thread, a lock) would otherwise
            // freeze the poll and the result limit with it. Its task is also cut at the answer timeout. Throwing, faulting, cancelling and timing out are all
            // "no answer", and the run then ends the way it chose.
            var asked = Task.Run(() => ask(prompt, token), token);
            Choice = answerTimeout is { } limit && limit > TimeSpan.Zero ? asked.WaitAsync(limit, time, token) : asked;
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

    /// <summary>How long the end of a wait lets the notices already given finish: a handler that blocks costs the run this much, never more.</summary>
    private static readonly TimeSpan NoticeDrainTimeout = TimeSpan.FromSeconds(2);

    private void Notify(StallState stall, RunNotice notice)
    {
        if (settings.OnNotice is { } handler)
        {
            stall.Enqueue(handler, notice);
        }
    }

    // Issue #1086: each NEW progress in the status.json this look read goes to the run page. A UI that throws must never end a run.
    private void PublishProgress(string jobId, WorkerProgressTracker tracker, string? statusText)
    {
        if (settings.OnProgress is not { } handler || statusText is null)
        {
            return;
        }

        if (tracker.Next(WorkerStatusReader.TryParseStatus(statusText)) is { } progress)
        {
            try
            {
                handler(jobId, progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The page is not the run.
            }
        }
    }

    private async Task<string> AwaitResultCoreAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken, long? vmStartMark, StallState stall)
    {
        var key = WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json";
        var limit = settings.ResultTimeout ?? ResultWaitLimit(request.Spec.MaxRunDuration);
        var clockStart = settings.StartClock();
        string? lastTransient = null;
        long? heartbeatMark = null; // when the row last took a heartbeat (monotonic), so it is written at most once a minute
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
            settings.StageDeadlines,
            settings.ProgressStallTimeout);
        var progressTracker = new WorkerProgressTracker();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var text = await calls.TryReadTextAsync(request.ResolvedScope, bucket, key, cancellationToken).ConfigureAwait(false);
                if (text is not null)
                {
                    return text;
                }

                var vm = await calls.CallAsync(token => compute.GetVmAsync(request.ResolvedScope, request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                Observe(request.JobId, vm);
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
                    text = await calls.TryReadTextAsync(request.ResolvedScope, bucket, key, cancellationToken).ConfigureAwait(false);
                    if (text is not null)
                    {
                        return text;
                    }

                    // Only a VM that is GONE (not merely stopped or preempted) can be one the platform deleted at its limit.
                    throw await VmLostAsync(request, bucket, zone, reason, cancellationToken, vm is null && vmCreatedAt is { } clockStartedAt ? new VmClockBasis(clockStartedAt, platform) : null).ConfigureAwait(false);
                }

                // RUNNING is not working: the VM being up says nothing about the job (CLAUDE.md Critical Pitfalls). The worker's
                // heartbeat in status.json is the health signal; a dead or frozen worker process ends the run here, not at the result limit.
                var statusText = await calls.TryReadTextAsync(request.ResolvedScope, bucket, statusKey, cancellationToken).ConfigureAwait(false);
                var progressText = watch.NeedsProgress ? await calls.TryReadTextAsync(request.ResolvedScope, bucket, progressKey, cancellationToken).ConfigureAwait(false) : null;
                var verdict = watch.Observe(statusText, progressText, settings.ElapsedSince(clockStart), settings.TimeProvider.GetUtcNow());
                heartbeatMark = await RecordHeartbeatAsync(request.JobId, watch.NewHeartbeatAt, heartbeatMark, cancellationToken).ConfigureAwait(false);
                PublishProgress(request.JobId, progressTracker, statusText);
                if (watch.TakeSlowNotice() is { } slow)
                {
                    Notify(stall, new RunNotice(request.JobId, RunNoticeKind.SlowStage, slow.Stage, slow.InStage, slow.Deadline));
                }

                // Issue #675: with a stall handler the verdict is a question for the person (Stop, Delete, Keep waiting), asked once while it is open; the polling for
                // result.json goes on meanwhile. A box with no GPU is never asked about (it is deleted at once, as startup.sh does).
                var forceStop = false;
                var forceDelete = false;
                if (verdict is { DeleteVm: true } && stall.IsOpen)
                {
                    // A box with no GPU is deleted at once whatever a question open about something else was waiting for: withdraw the question, the delete below wins.
                    stall.Close();
                    Notify(stall, new RunNotice(request.JobId, RunNoticeKind.StallCleared));
                }

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
                            case StallChoice.EndAsRunChose:
                            default:
                                verdict = asked; // no answer (or none wanted): the default end, never a VM left billing
                                break;
                        }
                    }
                    else if (verdict is null)
                    {
                        stall.Close();
                        Notify(stall, new RunNotice(request.JobId, RunNoticeKind.StallCleared));
                    }
                    else
                    {
                        verdict = null; // the question is already open: not repeated
                    }
                }
                else if (verdict is not null && !verdict.DeleteVm && settings.OnStall is { } ask)
                {
                    var waited = settings.ElapsedSince(clockStart);
                    stall.Open(ask, new StallPrompt(request.JobId, verdict.Code, verdict.Message, verdict.Stage, waited, RunNameOf(request), request.Spec.VmName), verdict, cancellationToken, settings.StallAnswerTimeout, settings.TimeProvider);
                    Notify(stall, new RunNotice(request.JobId, RunNoticeKind.StallDetected, verdict.Stage, Code: verdict.Code));
                    verdict = null;
                }

                if (verdict is not null)
                {
                    // A worker writes result.json BEFORE it stops its heartbeat: one last look before calling it dead.
                    text = await calls.TryReadTextAsync(request.ResolvedScope, bucket, key, cancellationToken).ConfigureAwait(false);
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

    /// <summary>What a stall question calls the run: its first input's file name, or its accession (never a path: the app's own staging folder is not the user's word for it).</summary>
    private static string? RunNameOf(CloudJobRequest request)
        => request.Inputs.FirstOrDefault(i => !i.IsAnnotation) is { } first ? (first.IsAccession ? first.Accession : first.FileName) : null;

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
        var workerCode = await TryReadWorkerErrorCodeAsync(request.ResolvedScope, bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "status.json", cancellationToken).ConfigureAwait(false);
        var mapped = RunErrorCodes.ForWorkerStatusCode(workerCode);

        // #89: a VM that was seen created, is gone, and whose maxRunDuration has now passed was deleted by Compute Engine at its limit
        // (Hard Rule 10's backstop), not lost. Issue #691: "now passed" is judged on the platform's clock (the last server time read plus the monotonic time since), because the laptop's clock may be an hour out; the platform's time is aged with a MONOTONIC interval so a wall-clock step cannot move it; with no plausible server time the laptop's clock is used. THEORY (unverified): the platform deletes at created + maxRunDuration on a real instance.
        var pastLimit = clock is { } basis && PlatformNow(basis.Reading) >= basis.RunClockStart + request.Spec.MaxRunDuration;

        // Issue #91: a Spot VM is a preemption only on POSITIVE evidence from Compute itself (a compute.instances.preempted zone operation on the instance). A VM that is
        // merely gone or stopped proves nothing: the person's Delete, a cancel, an admin, a zone outage and a limit with no clock all look the same, and a wrong guess is a paid
        // on-demand retry. Nothing is asked when this app ended the VM itself or the worker already said why it stopped. A lookup that fails is "not confirmed": the run fails
        // as a lost VM (its message names the action: start the run again). A preempted VM is deleted whatever the user chose for after a run.
        var preempted = false;
        var lookupFailed = false;
        _seenIncarnations.TryRemove(request.JobId, out var seen);
        if (request.Spec.Spot && mapped is null && !pastLimit && !terminator.AppRequestedEnd(request.JobId))
        {
            // Bound to this VM's own life: the id it was seen with, else the moment it was created (when the run knows it), else nothing (and then nothing is confirmed).
            var incarnation = seen ?? new VmIncarnation(null, clock?.RunClockStart);
            for (var look = 1; ; look++)
            {
                try
                {
                    preempted = await calls.CallAsync(token => compute.WasPreemptedAsync(request.ResolvedScope, request.Spec.VmName, zone, incarnation, token), cancellationToken).ConfigureAwait(false);
                }
                catch (CloudOperationException)
                {
                    lookupFailed = true;
                    break;
                }

                // Compute may record the operation a moment after the instance is gone: look again before concluding "not preempted".
                if (preempted || look >= settings.PreemptionLookupAttempts)
                {
                    break;
                }

                await settings.DelayAsync(settings.PreemptionLookupInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        var note = await terminator.EndVmAfterFailureAsync(request, zone, forceDelete: preempted).ConfigureAwait(false);
        return VmEndNotes.Apply(
            preempted
                ? new RunFailureException(CloudErrorKind.Other, RunErrorCodes.VmUnhealthy, reason ?? "The Spot VM was taken back by Google.") { SpotPreempted = true }
                : lookupFailed
                ? new RunFailureException(CloudErrorKind.Other, RunErrorCodes.VmUnhealthy, (reason ?? "The VM is no longer running.") + " Whether Google took the Spot VM back could not be checked, so the run was not started again.")
                : mapped is not null
                ? new RunFailureException(CloudErrorKind.Other, mapped, $"The worker reported {workerCode} in status.json and no result.json followed. {reason}")
                : pastLimit
                    ? new RunFailureException(CloudErrorKind.Other, RunErrorCodes.RunTimeLimit, $"The VM was gone after its maxRunDuration of {request.Spec.MaxRunDuration.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes and no result.json followed. {reason}")
                    : new RunFailureException(CloudErrorKind.Other, RunErrorCodes.VmUnhealthy, (reason ?? "The VM is no longer running.") + (workerCode is null ? string.Empty : $" status.json reported {workerCode}.")),
            note);
    }

    /// <summary>The <c>error.code</c> in <c>status.json</c>, or null when it is absent, unreadable or the look itself failed (a diagnostic, never worth failing the run over).</summary>
    private async Task<string?> TryReadWorkerErrorCodeAsync(CloudScope scope, string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var text = await calls.TryReadTextAsync(scope, bucket, key, cancellationToken).ConfigureAwait(false);
            return text is null ? null : WorkerResultReader.TryReadStatusErrorCode(text);
        }
        catch (CloudOperationException)
        {
            return null;
        }
    }
}
