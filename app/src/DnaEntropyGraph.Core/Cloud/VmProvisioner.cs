using System.Globalization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The zone ladder, and the ONLY owner of the in-flight create state: creates this instance has started and not seen
/// finish, by task. A create the caller gave up on (deadline, cancel) can still land afterwards; the cancel path
/// (<see cref="VmCanceller"/>) and the ladder wait for these through <see cref="SettleInflightCreatesAsync"/> before they
/// record a terminal state. Nothing else touches the dictionary.
/// </summary>
internal sealed class VmProvisioner(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings, VmTerminator terminator, Func<string, Task<bool>>? jobIsFinished = null, IQuotaGateway? quotaGateway = null, Func<RunProgress, CancellationToken, Task<string?>>? bucketLocation = null, Func<RunProgress, string, CancellationToken, Task>? claimZone = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, string> _inflightCreates = new();

    /// <summary>The GPU machine families (G2 = L4, A2 = A100, A3 = H100); anything else is a CPU smoke VM.</summary>
    public static bool ExpectsGpu(string machineType)
        => machineType.StartsWith("g2-", StringComparison.Ordinal)
           || machineType.StartsWith("a2-", StringComparison.Ordinal)
           || machineType.StartsWith("a3-", StringComparison.Ordinal);

    /// <summary>
    /// Issue #261: the spec to create, with the startup script and the
    /// <c>deg-*</c> attributes attached once the bucket name is known. A
    /// request with no worker image creates the VM without them (nothing
    /// would run the job; the caller owns supplying an image). An invalid
    /// value throws here, before any VM exists, and <see cref="CloudJobRunner.RunAsync"/>
    /// records the run as Failed.
    /// </summary>
    public static VmSpec WithStartupMetadata(CloudJobRequest request, string bucket)
    {
        if (request.WorkerImage is null)
        {
            return request.Spec;
        }

        var metadata = StartupMetadata.Build(
            bucket,
            request.JobId,
            request.WorkerImage,
            ExpectsGpu(request.Spec.MachineType),
            request.AfterTask,
            request.Spec.MaxRunDuration);
        return request.Spec with { Metadata = metadata };
    }

    /// <summary>
    /// Issue #257's reconciler: find an existing VM for this job id first,
    /// across every zone, and adopt it - never attempt a new zone until
    /// that lookup comes back empty. Only once nothing is found does this
    /// walk <see cref="CloudJobRequest.Zones"/> in order, using
    /// <see cref="OperationPoller"/> to turn each attempt's thrown
    /// <see cref="CloudOperationException"/> (today, from
    /// <see cref="DnaEntropyGraph.Cloud.FakeGcp"/>'s synchronous throw; a
    /// real gateway's LRO would instead report "not done yet" across
    /// several polls before resolving) into a classified outcome and apply
    /// docs/cloud_design.md section 5's abort-vs-continue rule.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(CloudJobRequest request, VmSpec spec, RunProgress progress, CancellationToken cancellationToken)
        => await ProvisionAsync(request, _ => Task.FromResult(spec), progress, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// As above, but the spec is made by <paramref name="completeSpec"/> only once the adoption lookup has found nothing, so
    /// whatever it needs (issue #606: the worker identity, an IAM call that can fail) is never asked of a run that adopts
    /// a VM, and a failure there can never end a healthy VM. It runs once, before the first create.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(CloudJobRequest request, Func<CancellationToken, Task<VmSpec>> completeSpec, RunProgress progress, CancellationToken cancellationToken)
    {
        var existing = await calls.CallAsync(token => compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            progress.VmMayExist = true;

            // A crash in the middle of a parallel batch can leave several VMs under the one job id: keep one, delete and verify the rest, or
            // they bill until their maxRunDuration and write into the same job prefix.
            var (kept, note) = await KeepOneOwnedVmAsync(request, existing, cancellationToken).ConfigureAwait(false);
            if (note is not null)
            {
                return ProvisionResult.Failed(CloudErrorKind.Other, "More than one VM was found for this job and an extra could not be deleted.", note, RunErrorCodes.For(CloudErrorKind.Other));
            }

            progress.VmName = kept.Name;
            await ClaimAsync(progress, kept.Zone, cancellationToken).ConfigureAwait(false);
            return ProvisionResult.Ok(kept.Zone, kept.Name);
        }

        var spec = await completeSpec(cancellationToken).ConfigureAwait(false);

        // Issue #94: a stopped VM this installation left behind is started for this job before a new one is created.
        if (await TryReuseAsync(request, spec, progress, cancellationToken).ConfigureAwait(false) is { } reuse)
        {
            return reuse;
        }

        // One value for both the create's own timeout and the poller's deadline, so they cannot drift apart: the create's
        // timeout must be the one that fires first (it records createTimedOut), and the poller's deadline is only the
        // backstop at the same instant.
        var plan = await PlanZonesAsync(request, spec, progress, cancellationToken).ConfigureAwait(false);
        if (spec.FlexStart && plan.Zones.Count > 1)
        {
            // Issue #92: a queued request waits in the zone it was made in, so the ladder would sit in its first zone for the whole wait and never reach the rest.
            // One zone (the best candidate) is asked; a wait that runs out is reported as the stockout it is.
            plan = plan with { Zones = [plan.Zones[0]] };
        }

        return await RunLadderAsync(request, spec, plan, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One zone's create attempt: the create under its own timeout, the classification of what it threw, and the clean-up of ITS zone only
    /// (a clean-up by job label would delete the winner of a parallel batch, #670). Never throws for a cloud failure.
    /// </summary>
    private async Task<ZoneAttempt> TryZoneAsync(CloudJobRequest request, VmSpec spec, AttemptState state, RunProgress progress, CancellationToken cancellationToken)
    {
        var zone = state.Zone;
        // Issue #92: a queued (flex-start) create is bounded by the wait it asked for plus a margin, never by the ordinary 30 s.
        var createTimeout = spec.FlexStart && spec.FlexStartWait is { } flexWait ? flexWait + settings.FlexStartMargin : settings.CreateTimeout;
        {
            // The kind the gateway (or the resilience pipeline in front of
            // it) already decided. Re-deriving it from the error text would
            // turn the pipeline's Network (retries exhausted, breaker open)
            // into Other, walk on to the next zone and end as a Stockout.
            CloudErrorKind? thrownKind = null;
            var createTimedOut = false;
            Task<VmDescriptor>? createTask = null;
            progress.VmMayExist = true;
            if (spec.FlexStart)
            {
                NotifyWaiting(request.JobId, RunNoticeKind.WaitingForGpu);
            }

            var outcome = await OperationPoller.PollAsync<VmDescriptor>(
                async _ =>
                {
                    try
                    {
                        // The create is bounded here by its own timeout (WaitAsync), which must stay the one that fires
                        // first and records createTimedOut, so it takes the caller's token, not the poller's deadline
                        // token (the same instant, so the poller's would win and hide the abandoned create).
                        progress.VmStartMark = settings.StartClock();
                        createTask = compute.CreateVmAsync(spec, zone, cancellationToken);
                        state.Create = createTask;
                        TrackInflightCreate(request.JobId, createTask);
                        var vm = await createTask.WaitAsync(createTimeout, settings.TimeProvider, cancellationToken).ConfigureAwait(false);
                        return new OperationPoll<VmDescriptor>(true, vm, null);
                    }
                    catch (TimeoutException)
                    {
                        createTimedOut = true;
                        thrownKind = CloudErrorKind.Network;
                        return new OperationPoll<VmDescriptor>(true, null, new CloudError(OperationPoller.TimeoutCode, null, "Creating the VM timed out."));
                    }
                    catch (CloudOperationException ex)
                    {
                        thrownKind = ex.Kind;
                        return new OperationPoll<VmDescriptor>(true, null, ex.Error);
                    }
                },
                createTimeout,
                cancellationToken,
                time: settings.TimeProvider).ConfigureAwait(false);

            if (spec.FlexStart)
            {
                NotifyWaiting(request.JobId, RunNoticeKind.WaitingForGpuEnded);
            }

            if (createTimedOut)
            {
                // The request may have been accepted even though no answer came, and can still land. Wait for it to
                // settle (bounded) and delete whatever it made BEFORE the run is allowed to end or try another zone;
                // if it cannot be confirmed the run says so (vm_end_unconfirmed). The VM's own maxRunDuration
                // (Hard Rule 10) is the backstop.
                var settleNote = await SettleAbandonedCreateAsync(request, createTask!, zone).ConfigureAwait(false);
                if (settleNote is not null)
                {
                    return ZoneAttempt.Aborted(zone, ProvisionResult.Failed(CloudErrorKind.Network, outcome.Error!.Message, settleNote));
                }
            }

            if (outcome.Success)
            {
                return ZoneAttempt.Win(zone);
            }

            var kind = thrownKind ?? CloudErrorClassifier.Classify(outcome.Error!);

            if (kind == CloudErrorKind.AlreadyExists)
            {
                // The exact resource this attempt would have created is
                // already there under someone else's create - adopt it
                // rather than treat this zone as failed.
                var vm = await calls.CallAsync(token => compute.GetVmAsync(request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                if (vm is not null)
                {
                    return ZoneAttempt.Win(zone);
                }
            }

            // A create can fail AFTER the server accepted it (the ResilientGateways retry, a response lost on the wire, an
            // internal error answered late): look in THIS zone and delete whatever landed before the ladder moves on or the
            // run is recorded, or a zone's VM is left billing beside the next zone's. A timed-out create was settled above.
            if (!createTimedOut && kind is CloudErrorKind.Network or CloudErrorKind.Other or CloudErrorKind.Stockout)
            {
                var sweepNote = await terminator.EndVmAfterFailureAsync(request, zone, forceDelete: true).ConfigureAwait(false);
                if (sweepNote is not null)
                {
                    return ZoneAttempt.Aborted(zone, ProvisionResult.Failed(kind, outcome.Error!.Message, sweepNote, RunErrorCodes.For(outcome.Error, kind)));
                }
            }

            if (VmFacts.IsProjectWide(kind) || kind == CloudErrorKind.Network)
            {
                // docs/cloud_design.md section 5: billing/API/permission/org
                // policy abort the WHOLE ladder immediately - retrying a
                // different zone cannot fix a project-wide problem.
                return ZoneAttempt.Aborted(zone, ProvisionResult.Failed(kind, outcome.Error!.Message, code: RunErrorCodes.For(outcome.Error, kind)));
            }

            // Quota / stockout / other: per-zone or per-region, worth trying
            // the next zone (CLAUDE.md: "quota is not stockout", but neither
            // one aborts the ladder). Network is not in this list: another
            // zone cannot fix an unreachable API, and reporting it as a
            // stockout after trying them all is the bug this guards against.
            return ZoneAttempt.Lost(zone, kind);
        }
    }

    /// <summary>
    /// Issue #92: tells whoever shows the run that it is (or is no longer) waiting for a card. Off the run's own path: a handler that blocks or throws costs the run nothing.
    /// </summary>
    private void NotifyWaiting(string jobId, RunNoticeKind kind)
    {
        if (settings.OnNotice is not { } handler)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                handler(new RunNotice(jobId, kind));
            }
            catch (Exception)
            {
                // A notice is advice for the screen; the run goes on without it.
            }
        });
    }

    /// <summary>The zones to try, in order, and whether the quota read says there is none anywhere (#670).</summary>
    private readonly record struct ZonePlan(IReadOnlyList<string> Zones, bool QuotaZeroEverywhere);

    /// <summary>
    /// Issue #670. The zone universe is what the gateway says offers the machine's accelerator (<see cref="ZoneCandidates.AcceleratorFor"/>:
    /// the tier the request asked for, never a bigger one); the candidates are <see cref="ZoneCandidates.Build"/> over it with the quota read,
    /// the request's first zone as the preference and the bucket's location. A machine with no GPU, an offer that cannot be read or that comes
    /// back empty, and a quota that cannot be read each fall back to "do not narrow": unknown is not none, so the request's own zones are used.
    /// A zone the user pinned (<see cref="RunOptions.ZonePreference"/>) is the only candidate.
    /// </summary>
    private async Task<ZonePlan> PlanZonesAsync(CloudJobRequest request, VmSpec spec, RunProgress progress, CancellationToken cancellationToken)
    {
        // A zone the user chose (RunOptions.ZonePreference: Auto is null) is a constraint, not a preference: data may have to stay in one
        // place (#149). The ladder never leaves it; a stockout there is reported as one and the user picks Auto or another zone.
        if (request.Options?.ZonePreference is { Length: > 0 } pinned)
        {
            return new ZonePlan([pinned.Trim()], false);
        }

        var accelerator = ZoneCandidates.AcceleratorFor(spec.MachineType);
        if (accelerator is null)
        {
            return new ZonePlan(request.Zones, false);
        }

        IReadOnlyList<string> offered;
        try
        {
            offered = await calls.CallAsync(token => compute.ListAcceleratorZonesAsync(accelerator, token), cancellationToken, settings.QuotaReadTimeout).ConfigureAwait(false);
        }
        catch (CloudOperationException)
        {
            return new ZonePlan(request.Zones, false);
        }

        if (offered.Count == 0)
        {
            return new ZonePlan(request.Zones, false);
        }

        QuotaReadResult? quota = null;
        if (quotaGateway is not null)
        {
            try
            {
                quota = await calls.CallAsync(token => quotaGateway.ReadQuotaAsync(spec.ProjectId, token), cancellationToken, settings.QuotaReadTimeout).ConfigureAwait(false);
            }
            catch (CloudOperationException)
            {
                // A quota that cannot be read is unknown, never zero: no zone is dropped for it.
            }
        }

        string? location = null;
        if (bucketLocation is not null)
        {
            try
            {
                location = await bucketLocation(progress, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException)
            {
                // The location only orders the zones; unknown orders them as offered.
            }
        }

        var built = ZoneCandidates.Build(new ZoneCandidateInput(offered, [], request.Zones.Count > 0 ? request.Zones[0] : null, null, location, quota, QuotaMetrics.ForAccelerator(accelerator)));
        return built.Zones.Count == 0 ? new ZonePlan(request.Zones, false) : new ZonePlan(built.Zones, built.QuotaZeroEverywhere);
    }

    /// <summary>How many creates run together in the nth batch (0-based): three one at a time for fast feedback in the common case, then 3, 5 and batches of 7 in parallel.</summary>
    private static int BatchSize(int batch) => batch switch { < 3 => 1, 3 => 3, 4 => 5, _ => 7 };

    /// <summary>
    /// Issue #670: the escalating ladder over the planned zones. Parallel attempts use the same VM name in different zones (names are zonal).
    /// A batch is acted on at its FIRST winner, not when its slowest attempt ends: the stragglers are cancelled, and every other zone of the
    /// batch (a zone another attempt landed in, or a straggler whose create may still land) is ended BY ZONE from what this batch knows, then
    /// a listing by label sweeps for anything else as a backstop (a listing can lag, so it is never the only look). Every extra that cannot be
    /// confirmed gone is named in the note. The kept zone is recorded in the bucket (<c>claimZone</c>) so a VM that boots in another zone anyway
    /// refuses to touch the job prefix (<c>worker/vm/startup.sh</c>). A project-wide error aborts at the first batch that sees it (after the
    /// batch is settled; a winner from the same batch is still kept). The tier is never escalated here: that needs the user's click.
    /// </summary>
    private async Task<ProvisionResult> RunLadderAsync(CloudJobRequest request, VmSpec spec, ZonePlan plan, RunProgress progress, CancellationToken cancellationToken)
    {
        var lost = new List<CloudErrorKind>();
        var next = 0;
        for (var batch = 0; next < plan.Zones.Count; batch++)
        {
            var zones = plan.Zones.Skip(next).Take(BatchSize(batch)).ToList();
            next += zones.Count;
            progress.VmMayExist = true;

            if (zones.Count == 1)
            {
                // Written BEFORE the create: a claim left by an earlier parallel win names another zone, and this VM reads it at boot.
                await ClaimAsync(progress, zones[0], cancellationToken).ConfigureAwait(false);
                var single = await TryZoneAsync(request, spec, new AttemptState(zones[0]), progress, cancellationToken).ConfigureAwait(false);
                if (single.Won)
                {
                    return ProvisionResult.Ok(single.Zone);
                }

                if (single.Abort is not null)
                {
                    return single.Abort.Value;
                }

                lost.Add(single.Kind!.Value);
                continue;
            }

            var parallel = await RunParallelBatchAsync(request, spec, zones, progress, cancellationToken).ConfigureAwait(false);
            if (parallel.Result is { } decided)
            {
                return decided;
            }

            lost.AddRange(parallel.Lost);
        }

        // Every zone failed. What the creates answered decides the kind first: a real stockout is the transient one worth waiting out, and a
        // quota READ of zero must not hide it (the read can under-report). Quota the user can fix is reported when every failure was a quota
        // one, or, when the creates said neither, when the read says there is none anywhere.
        var kind = lost.Any(k => k == CloudErrorKind.Stockout) ? CloudErrorKind.Stockout
            : lost.Count > 0 && lost.All(k => k == CloudErrorKind.Quota) ? CloudErrorKind.Quota
            : plan.QuotaZeroEverywhere ? CloudErrorKind.Quota
            : CloudErrorKind.Stockout;
        var detail = kind == CloudErrorKind.Quota ? " The project has no GPU quota in them: raising it is something the user can do." : string.Empty;
        return ProvisionResult.Failed(kind, $"No zone in [{string.Join(", ", plan.Zones)}] could provision '{request.Spec.VmName}'.{detail}");
    }

    /// <summary>What one parallel batch came to: a decided result (a kept winner, or an abort), or the kinds its attempts lost with.</summary>
    private readonly record struct BatchOutcome(ProvisionResult? Result, IReadOnlyList<CloudErrorKind> Lost);

    private async Task<BatchOutcome> RunParallelBatchAsync(CloudJobRequest request, VmSpec spec, IReadOnlyList<string> zones, RunProgress progress, CancellationToken callerToken)
    {
        // No zone is the kept one yet: clear any claim an earlier provision left, so no VM of this batch reads a stale zone and deletes itself.
        await ClaimAsync(progress, string.Empty, callerToken).ConfigureAwait(false);
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var states = zones.Select(z => new AttemptState(z)).ToList();
        var running = states.Select(s => AttemptAsync(request, spec, s, progress, batchCts.Token, callerToken)).ToList();
        var pending = running.ToList();
        var results = new List<ZoneAttempt>();
        ZoneAttempt? winner = null;
        ProvisionResult? abort = null;

        // Act on the first attempt to finish with a win; attempts that finished at the same moment are taken in candidate order.
        while (pending.Count > 0 && winner is null)
        {
            await Task.WhenAny(pending).ConfigureAwait(false);
            var done = pending.Where(t => t.IsCompleted).OrderBy(t => running.IndexOf(t)).ToList();
            foreach (var task in done)
            {
                pending.Remove(task);
                var attempt = await task.ConfigureAwait(false);
                results.Add(attempt);
                if (attempt.Won && winner is null)
                {
                    winner = attempt;
                }
                else if (attempt.Abort is not null)
                {
                    abort ??= attempt.Abort;
                }
            }
        }

        // The claim comes the moment a winner exists, before anything is cancelled or deleted: an extra that is mid-boot reads it and stands down,
        // and it must be there in the very case it exists for, an extra that cannot be confirmed gone.
        if (winner is not null)
        {
            await ClaimAsync(progress, winner.Value.Zone, callerToken).ConfigureAwait(false);
        }

        // Stragglers (only when a winner ended the wait early): cancel them, then wait for their creates to settle.
        var stragglers = states.Where(s => !results.Any(r => r.Zone == s.Zone)).ToList();
        if (pending.Count > 0)
        {
            await batchCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        if (winner is null)
        {
            // Nothing won. Whatever an attempt cleaned up was cleaned in its own zone already.
            if (abort is not null)
            {
                return new BatchOutcome(abort, []);
            }

            return new BatchOutcome(null, results.Select(r => r.Kind ?? CloudErrorKind.Other).ToList());
        }

        // Every zone of the batch but the winner's is ended by name and zone: the ones another attempt landed in, and the stragglers.
        var notes = new List<string>();
        foreach (var other in states.Where(s => s.Zone != winner.Value.Zone))
        {
            string? note;
            if (stragglers.Contains(other) && other.Create is { } createTask)
            {
                // A cancelled create can still land: wait for it (bounded), then delete what it made in this zone.
                note = await SettleAbandonedCreateAsync(request, createTask, other.Zone).ConfigureAwait(false);
            }
            else
            {
                // Another attempt that landed, or one that failed (and cleaned its own zone) or never started: look once, by zone.
                var error = await terminator.EnsureVmEndedAsync(request, other.Zone, AfterTaskAction.Delete, CancellationToken.None).ConfigureAwait(false);
                note = error is null ? null : " (VM end not confirmed: " + error + ")";
            }

            if (note is not null)
            {
                notes.Add(note);
            }
        }

        // Backstop: a VM the batch did not know about (a create that landed under another name's answer) is found by label. A listing can
        // lag or fail, so it adds to what is known and never replaces it.
        var sweep = await DeleteOtherZonesAsync(request, winner.Value.Zone, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (sweep is not null)
        {
            notes.Add(sweep);
        }

        if (notes.Count > 0)
        {
            var all = await terminator.EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
            return new BatchOutcome(
                ProvisionResult.Failed(CloudErrorKind.Other, "More than one VM was created for this job in a parallel attempt and an extra could not be deleted.", string.Concat(notes) + all, RunErrorCodes.For(CloudErrorKind.Other)),
                []);
        }

        return new BatchOutcome(ProvisionResult.Ok(winner.Value.Zone), []);
    }

    /// <summary>
    /// Issue #670: records the zone this job's VM is meant to be in (<c>jobs/&lt;job&gt;/control/zone</c>), or clears the record with an empty
    /// <paramref name="zone"/> (no zone is the kept one yet). Every provision sets it before the VM it makes can read it, so a claim left by an
    /// earlier run of the job never makes a legitimate VM delete itself. Best effort: <see cref="GatewayCalls"/> turns a call that outlives its
    /// deadline into a <see cref="CloudOperationException"/> (Network), which is swallowed here like any other failed write; the caller
    /// cancelling still propagates.
    /// </summary>
    private async Task ClaimAsync(RunProgress progress, string zone, CancellationToken cancellationToken)
    {
        if (claimZone is null)
        {
            return;
        }

        try
        {
            await claimZone(progress, zone, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException)
        {
            // The extras are deleted by API regardless; the record only stops one that is mid-boot or lands late.
        }
    }

    /// <summary>One attempt of a parallel batch. A cancel of the BATCH (a winner was found) ends it as a lost attempt; the caller cancelling still propagates.</summary>
    private async Task<ZoneAttempt> AttemptAsync(CloudJobRequest request, VmSpec spec, AttemptState state, RunProgress progress, CancellationToken batchToken, CancellationToken callerToken)
    {
        try
        {
            return await TryZoneAsync(request, spec, state, progress, batchToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (batchToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            return ZoneAttempt.Lost(state.Zone, CloudErrorKind.Other);
        }
    }

    /// <summary>Deletes every VM carrying this job's label outside <paramref name="keepZone"/> and verifies each is gone. Null when none is left, else why one is (every one that is not gone is named).</summary>
    private async Task<string?> DeleteOtherZonesAsync(CloudJobRequest request, string keepZone, CancellationToken cancellationToken)
    {
        IReadOnlyList<VmDescriptor> owned;
        try
        {
            owned = await calls.CallAsync(token => compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex)
        {
            return $" (VM end not confirmed: the VMs for this job could not be looked up: {ex.Message})";
        }

        return await EndExtrasAsync(request, owned.Where(v => !string.Equals(v.Zone, keepZone, StringComparison.Ordinal)).ToList(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes each of <paramref name="extras"/> under its own name and zone and verifies it is gone. Null when all are, else one note naming every one that is not.</summary>
    private async Task<string?> EndExtrasAsync(CloudJobRequest request, IReadOnlyList<VmDescriptor> extras, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        foreach (var vm in extras)
        {
            var error = await terminator.EnsureVmEndedAsync(request, vm.Zone, AfterTaskAction.Delete, cancellationToken, vm.Name).ConfigureAwait(false);
            if (error is not null)
            {
                notes.Add(" (VM end not confirmed: " + error + ")");
            }
        }

        return notes.Count == 0 ? null : string.Concat(notes);
    }

    /// <summary>
    /// A resume that finds several VMs labelled with the job (a crash in the middle of a parallel batch): keeps ONE, preferring a running VM,
    /// then a booting one, then the earliest in the request's zone order, and deletes and verifies the rest. Returns the VM to continue on and
    /// a note that is null when every extra is confirmed gone.
    /// </summary>
    public async Task<(VmDescriptor Kept, string? Note)> KeepOneOwnedVmAsync(CloudJobRequest request, IReadOnlyList<VmDescriptor> owned, CancellationToken cancellationToken)
    {
        var zoneOrder = request.Zones.ToList();
        var kept = owned
            .OrderBy(v => v.Status == "RUNNING" ? 0 : VmFacts.IsBooting(v) ? 1 : 2)
            .ThenBy(v => zoneOrder.IndexOf(v.Zone) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(v => v.Zone, StringComparer.Ordinal)
            .First();
        if (owned.Count == 1)
        {
            return (kept, null);
        }

        var note = await EndExtrasAsync(request, owned.Where(v => !ReferenceEquals(v, kept)).ToList(), cancellationToken).ConfigureAwait(false);
        return (kept, note);
    }

    /// <summary>
    /// Issue #94. Null means "provision a new VM": nothing eligible, or the start failed in a way a new VM can fix. A VM is eligible when
    /// it is this installation's (the listing is by label, Hard Rule 9), stopped, of the same model label and machine type, and the run
    /// that last used it has ended (a stopped VM of a run still underway is that run's). A start that fails is never a failed run by
    /// itself: the VM, which was claimed for this job by then, is ended by label so nothing is left billing, and the ladder takes over.
    /// A failure of a class a new VM cannot fix (billing off, the API off, a missing permission, an unreachable API) aborts like the ladder's.
    /// </summary>
    private async Task<ProvisionResult?> TryReuseAsync(CloudJobRequest request, VmSpec spec, RunProgress progress, CancellationToken cancellationToken)
    {
        // Issue #91: a stopped VM does not say whether it was created Spot, and a Spot run must start a Spot VM (and an on-demand one must not
        // start a Spot VM), so a Spot run never reuses one. THEORY (unverified): reuse of an on-demand VM by a later run after a Spot run left its own stopped VM is judged by machine type only.
        if (jobIsFinished is null || spec.Spot || spec.FlexStart)
        {
            return null;
        }

        IReadOnlyList<VmDescriptor> listed;
        try
        {
            listed = await calls.CallAsync(token => compute.ListByInstallationAsync(spec.InstallationId, token), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException)
        {
            // Not being able to look is not a reason to fail: the create path reports a project-wide problem itself.
            return null;
        }

        var model = spec.ToLabels()["model"];
        VmDescriptor? chosen = null;
        foreach (var vm in listed.OrderByDescending(v => v.StoppedAt ?? DateTimeOffset.MinValue))
        {
            if (!VmFacts.IsStopped(vm) || vm.Labels is null || vm.MachineType != spec.MachineType
                || !vm.Labels.TryGetValue("app", out var app) || app != VmSpec.AppLabelValue
                || !vm.Labels.TryGetValue("installation-id", out var owner) || owner != spec.InstallationId
                || !vm.Labels.TryGetValue("model", out var vmModel) || vmModel != model
                || !vm.Labels.TryGetValue("job-id", out var oldJob) || oldJob == request.JobId)
            {
                continue;
            }

            if (await jobIsFinished(oldJob).ConfigureAwait(false))
            {
                chosen = vm;
                break;
            }
        }

        if (chosen is null)
        {
            return null;
        }

        progress.VmMayExist = true;
        await ClaimAsync(progress, chosen.Zone, cancellationToken).ConfigureAwait(false); // before the start: a stale claim must not make this VM delete itself
        try
        {
            // Tracked like a create: a cancel that arrives between the claim and the start must wait for this to land, or the start
            // goes on after the cancel looked and leaves a billing VM under a Cancelled record.
            var started = await calls.CallAsync(
                token =>
                {
                    progress.VmStartMark = settings.StartClock();
                    var reuse = compute.ReuseVmAsync(spec, chosen.Name, chosen.Zone, token);
                    TrackInflightCreate(request.JobId, reuse);
                    return reuse;
                },
                cancellationToken,
                settings.CreateTimeout).ConfigureAwait(false);
            progress.VmName = started.Name;
            return ProvisionResult.Ok(started.Zone, started.Name);
        }
        catch (CloudOperationException ex)
        {
            // A Network failure is an UNKNOWN outcome (the composite is never replayed, and its last answer may have been lost after the
            // start worked): a VM this job's label now finds and that is booting or running is the reuse having succeeded. Adopt it.
            if (ex.Kind == CloudErrorKind.Network && await TryAdoptReusedAsync(request, cancellationToken).ConfigureAwait(false) is { } adopted)
            {
                progress.VmName = adopted.Name;
                return ProvisionResult.Ok(adopted.Zone, adopted.Name);
            }

            // The claim may already have been made: whatever carries this job's label is ended before anything else is tried.
            var note = await terminator.EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
            if (note is not null || VmFacts.IsProjectWide(ex.Kind) || ex.Kind == CloudErrorKind.Network)
            {
                return ProvisionResult.Failed(ex.Kind, ex.Error.Message, note, RunErrorCodes.For(ex.Error, ex.Kind));
            }

            return null;
        }
    }

    /// <summary>The one VM the job's label finds, when it is booting or running (the reuse's start worked and only its answer was lost); else null. A failure of the look is "not found".</summary>
    private async Task<VmDescriptor?> TryAdoptReusedAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var found = await calls.CallAsync(token => compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
            return found is [{ } only] && (only.Status == "RUNNING" || VmFacts.IsBooting(only)) ? only : null;
        }
        catch (CloudOperationException)
        {
            return null;
        }
    }
    /// <summary>Waits, bounded by <see cref="CloudRunSettings.CreateSettleTimeout"/>, for every create this instance started for the job to land or fail. False when one is still in flight.</summary>
    public async Task<bool> SettleInflightCreatesAsync(string jobId)
    {
        var pending = _inflightCreates.Where(kv => kv.Value == jobId).Select(kv => kv.Key).ToList();
        if (pending.Count == 0)
        {
            return true;
        }

        var all = Task.WhenAll(pending);
        var winner = await Task.WhenAny(all, Task.Delay(settings.CreateSettleTimeout, settings.TimeProvider)).ConfigureAwait(false);
        if (winner != all)
        {
            return false;
        }

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A create that failed made no VM; nothing to wait for or to report.
        }

        return true;
    }

    private void TrackInflightCreate(string jobId, Task createTask)
    {
        _inflightCreates[createTask] = jobId;
        _ = createTask.ContinueWith(
            t =>
            {
                _ = t.Exception;
                _inflightCreates.TryRemove(t, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// After a create timed out: wait for it to settle, then delete whatever landed (a VM that never ran has no disk
    /// worth keeping). Returns null when nothing is left, else a note that the VM end is not confirmed.
    /// </summary>
    private async Task<string?> SettleAbandonedCreateAsync(CloudJobRequest request, Task createTask, string zone)
    {
        var winner = await Task.WhenAny(createTask, Task.Delay(settings.CreateSettleTimeout, settings.TimeProvider)).ConfigureAwait(false);
        if (winner != createTask)
        {
            return $" (VM end not confirmed: a create request was still in flight after {settings.CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s and could not be cancelled)";
        }

        // This zone only: a label sweep would also delete the winner of a parallel batch (#670).
        return await terminator.EndVmAfterFailureAsync(request, zone, forceDelete: true).ConfigureAwait(false);
    }
}

/// <summary>One zone of a batch and the create call made in it (set once started), so a batch can settle a create it abandoned.</summary>
internal sealed class AttemptState(string zone)
{
    public string Zone { get; } = zone;

    public Task<VmDescriptor>? Create { get; set; }
}

/// <summary>What one zone's attempt came to (#670): it won, it lost in a way another zone can fix (<see cref="Kind"/>), or it ends the whole ladder (<see cref="Abort"/>).</summary>
internal readonly record struct ZoneAttempt(string Zone, bool Won, CloudErrorKind? Kind, ProvisionResult? Abort)
{
    public static ZoneAttempt Win(string zone) => new(zone, true, null, null);

    public static ZoneAttempt Lost(string zone, CloudErrorKind kind) => new(zone, false, kind, null);

    public static ZoneAttempt Aborted(string zone, ProvisionResult result) => new(zone, false, null, result);
}

internal readonly struct ProvisionResult
{
    private ProvisionResult(bool success, string? zone, CloudErrorKind? failureKind, string? failureMessage, string? vmEndNote, string? failureCode, string? vmName = null)
    {
        VmName = vmName;
        FailureCode = failureCode;
        Success = success;
        Zone = zone;
        FailureKind = failureKind;
        FailureMessage = failureMessage;
        VmEndNote = vmEndNote;
    }

    /// <summary>Set when a create the runner gave up on could not be confirmed ended.</summary>
    public string? VmEndNote { get; }

    public bool Success { get; }

    public string? Zone { get; }

    public CloudErrorKind? FailureKind { get; }

    public string? FailureMessage { get; }

    /// <summary>The code the run row records (issue #604): the error's own specific code (PERMISSION_ACTAS) when it has one, else the code of <see cref="FailureKind"/>.</summary>
    public string? FailureCode { get; }

    /// <summary>The name of the VM when it is not the one computed from the job id (a reused or adopted VM, #94); null when the caller knows it already.</summary>
    public string? VmName { get; }

    public static ProvisionResult Ok(string zone, string? vmName = null) => new(true, zone, null, null, null, null, vmName);

    public static ProvisionResult Failed(CloudErrorKind kind, string message, string? vmEndNote = null, string? code = null) => new(false, null, kind, message, vmEndNote, code ?? RunErrorCodes.For(kind));
}
