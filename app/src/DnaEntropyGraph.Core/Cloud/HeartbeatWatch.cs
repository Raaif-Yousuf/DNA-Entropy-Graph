using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What <see cref="HeartbeatWatch"/> decided: the run code, the diagnostic sentence, and whether the VM should be deleted rather than stopped.</summary>
internal sealed record HeartbeatVerdict(string Code, string Message, bool DeleteVm)
{
    /// <summary>The stage the worker was in when the verdict fired, or null when it had not reported one.</summary>
    public string? Stage { get; init; }
}

/// <summary>A stage that has used <see cref="HeartbeatWatch.SlowFraction"/> of its deadline (issue #675).</summary>
internal sealed record SlowStageNotice(string Stage, TimeSpan InStage, TimeSpan Deadline);

/// <summary>
/// The judgement that "RUNNING is not working" (CLAUDE.md Critical Pitfalls): the worker's heartbeat in <c>status.json</c> is
/// the only health signal. Fed what the waiter read on each poll, with the time since the wait began on the app's own
/// monotonic clock. The worker's <c>updatedAt</c> is never compared with the app's wall clock (the two machines can disagree
/// by minutes); only whether <c>heartbeatSeq</c>/<c>updatedAt</c> CHANGED between looks is used, so skew cannot fake or hide a death.
/// One instance per wait; holds that wait's observations.
/// </summary>
internal sealed class HeartbeatWatch(bool expectsGpu, TimeSpan firstHeartbeatTimeout, TimeSpan staleTimeout, IReadOnlyDictionary<string, TimeSpan>? stageDeadlines = null, TimeSpan? progressStallTimeout = null)
{
    /// <summary>How long the progress fields may stay unchanged in the running stage when none is passed (docs/job_contract.md section 5, issue #723).</summary>
    public static TimeSpan DefaultProgressStallTimeout { get; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long one stage may last, by <c>status.json</c> <c>stage</c> (docs/job_contract.md section 5, #90). A stage with no entry
    /// (<c>running</c>: its length depends on the input; <c>idle</c>; the terminal ones) has no deadline here: the result limit and the
    /// heartbeat judge it. <c>provisioning</c> is the create wait and is not a worker stage. THEORY (unverified): these are the
    /// contract's numbers, not measured on a real VM.
    /// </summary>
    public static IReadOnlyDictionary<string, TimeSpan> DefaultStageDeadlines { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        [WorkerStages.Booting] = TimeSpan.FromMinutes(8),
        [WorkerStages.Installing] = TimeSpan.FromMinutes(15),
        [WorkerStages.RestoringCache] = TimeSpan.FromMinutes(15),
        [WorkerStages.ModelLoading] = TimeSpan.FromMinutes(15),
        [WorkerStages.Uploading] = TimeSpan.FromMinutes(10),
    };

    /// <summary>The fraction of a stage's deadline after which the soft "taking longer than usual" notice is given (issue #675).</summary>
    public const double SlowFraction = 0.75;

    private string? _stage;
    private TimeSpan _stageSince;
    private bool _slowNoticedForStage;
    private SlowStageNotice? _slowNotice;
    private TimeSpan _firstHeartbeatBase;
    private bool _workerSeen;
    private bool _gpuVerified;
    private long? _lastSeq;
    private string? _lastUpdatedAt;
    private TimeSpan _lastChange;
    private bool _progressSeen;
    private string? _progressKey;
    private TimeSpan _progressChange;

    /// <summary>The worker's own write time (its status updatedAt) when the last <see cref="Observe"/> saw a heartbeat that is new (first seen, or its sequence or write time moved), else null: the run row records it as a sign of life (issue #109). The heartbeat's own time, never the app's clock, so a stale status.json found on a resume records its true age.</summary>
    public DateTimeOffset? NewHeartbeatAt { get; private set; }

    /// <summary>True when the caller should also read <c>progress.jsonl</c>: a GPU tier whose worker is up and whose GPU is not yet confirmed.</summary>
    public bool NeedsProgress => expectsGpu && _workerSeen && !_gpuVerified;

    /// <summary>Null while the worker looks healthy; otherwise why the run must end. <paramref name="statusJson"/>/<paramref name="progressJsonl"/> are null when not (yet) there.</summary>
    public HeartbeatVerdict? Observe(string? statusJson, string? progressJsonl, TimeSpan elapsed, DateTimeOffset? appNow = null)
    {
        NewHeartbeatAt = null;
        var status = statusJson is null ? null : WorkerStatusReader.TryParseStatus(statusJson);

        // The stage clock restarts whenever the stage NAME changes, whoever wrote it (the startup script writes booting and installing).
        // A stage already running when this watch first sees it (a resume, a reattach) has already used part of its allowance: the worker
        // reports when the stage began (stageStartedAt) and when it last wrote (updatedAt), both on ITS clock, so the difference is the
        // stage's age with no skew against the app's clock. A status without them (an older worker) starts the clock at this look.
        if (status?.Stage is { Length: > 0 } stage && !string.Equals(stage, _stage, StringComparison.Ordinal))
        {
            _stage = stage;
            _stageSince = elapsed - StageAgeAtLastWrite(status);
            _slowNoticedForStage = false;
        }

        if (status is { WorkerOwned: true })
        {
            if (!_workerSeen || status.HeartbeatSeq != _lastSeq || status.UpdatedAt != _lastUpdatedAt)
            {
                _lastChange = elapsed;
                // The worker's own write time when it has a readable one. With none, a heartbeat that advanced while THIS watch was looking is alive now by the app's
                // clock; the first look at a status.json (a resume may find an old one) proves nothing about now and records nothing.
                NewHeartbeatAt = DateTimeOffset.TryParse(status.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var wrote)
                    ? wrote
                    : _workerSeen ? appNow : null;
            }

            // Issue #723: the forward-progress signal (windowsDone, lastProgressAt) moves only when the compute thread does, unlike the heartbeat. Judged by CHANGE
            // between looks (with the stage), never against the app's clock. An older worker's status has neither field, so it has no progress signal at all.
            _progressSeen = status.WindowsDone is not null || status.LastProgressAt is not null;
            var progressKey = $"{status.Stage}|{status.WindowsDone}|{status.LastProgressAt}";
            if (_progressSeen && !string.Equals(progressKey, _progressKey, StringComparison.Ordinal))
            {
                _progressChange = elapsed;
            }

            _progressKey = progressKey;
            _workerSeen = true;
            _lastSeq = status.HeartbeatSeq;
            _lastUpdatedAt = status.UpdatedAt;
        }

        if (expectsGpu && _workerSeen && !_gpuVerified && progressJsonl is not null)
        {
            switch (WorkerStatusReader.ReadGpuReport(progressJsonl))
            {
                case WorkerGpuReport.Absent:
                    return new HeartbeatVerdict(
                        RunErrorCodes.GpuNotVisible,
                        "The worker's first progress line reported no GPU on a GPU machine type.",
                        DeleteVm: true);
                case WorkerGpuReport.Present:
                    _gpuVerified = true;
                    break;
            }
        }

        // Issue #675: a stage that has used 75 percent of its allowance is announced once (the caller reads it with TakeSlowNotice), long before it is judged.
        if (_stage is not null && !_slowNoticedForStage && DeadlineFor(_stage) is { } slowDeadline && elapsed - _stageSince >= TimeSpan.FromTicks((long)(slowDeadline.Ticks * SlowFraction)))
        {
            _slowNoticedForStage = true;
            _slowNotice = new SlowStageNotice(_stage, elapsed - _stageSince, slowDeadline);
        }

        // A worker whose heartbeat thread ticks while its main thread is stuck in one stage (#90, partly #522) is caught here: the
        // stage deadline does not care that the heartbeat moves.
        if (_stage is not null && DeadlineFor(_stage) is { } deadline && elapsed - _stageSince >= deadline)
        {
            return new HeartbeatVerdict(
                RunErrorCodes.WorkerStageStuck,
                $"The worker stayed in stage '{_stage}' for {Format(elapsed - _stageSince)}, past that stage's {Format(deadline)} deadline.",
                DeleteVm: false)
            { Stage = _stage };
        }

        // A heartbeat that ticks while the compute thread is wedged inside the running stage (a hung CUDA call, a deadlock; #522): the stage deadlines do not
        // cover running (its length depends on the input), so its progress fields do.
        var progressBound = progressStallTimeout ?? DefaultProgressStallTimeout;
        if (_workerSeen && _progressSeen && string.Equals(_stage, WorkerStages.Running, StringComparison.Ordinal) && elapsed - _progressChange >= progressBound)
        {
            return new HeartbeatVerdict(
                RunErrorCodes.WorkerProgressStalled,
                $"The worker's heartbeat is moving but it finished no window and changed no stage for {Format(elapsed - _progressChange)} (windowsDone {_progressKey?.Split('|')[1]}), past the {Format(progressBound)} progress bound.",
                DeleteVm: false)
            { Stage = _stage };
        }

        if (!_workerSeen)
        {
            return elapsed - _firstHeartbeatBase >= firstHeartbeatTimeout
                ? new HeartbeatVerdict(
                    RunErrorCodes.WorkerNoHeartbeat,
                    $"No worker heartbeat appeared in status.json within {Format(firstHeartbeatTimeout)}.",
                    DeleteVm: false)
                { Stage = _stage }
                : null;
        }

        return elapsed - _lastChange >= staleTimeout
            ? new HeartbeatVerdict(
                RunErrorCodes.WorkerHeartbeatStale,
                $"The worker's heartbeat in status.json has not changed for {Format(staleTimeout)} (last heartbeatSeq {_lastSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}).",
                DeleteVm: false)
            { Stage = _stage }
            : null;
    }

    /// <summary>The "taking longer than usual" notice the last <see cref="Observe"/> raised, once; reading it clears it.</summary>
    public SlowStageNotice? TakeSlowNotice()
    {
        var notice = _slowNotice;
        _slowNotice = null;
        return notice;
    }

    /// <summary>
    /// "Keep waiting" (issue #675): the user looked at a verdict and chose to wait, so every allowance restarts from <paramref name="elapsed"/>: the stage
    /// deadline, the stale-heartbeat window and the wait for a first heartbeat. The same verdict can fire again after a full allowance. The result limit
    /// is not the watch's and is unaffected, so waiting is never unbounded (Hard Rule 11).
    /// </summary>
    public void Snooze(TimeSpan elapsed)
    {
        _stageSince = elapsed;
        _lastChange = elapsed;
        _firstHeartbeatBase = elapsed;
        _progressChange = elapsed;
    }

    private static TimeSpan StageAgeAtLastWrite(WorkerStatusSnapshot status)
    {
        if (DateTimeOffset.TryParse(status.StageStartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var began)
            && DateTimeOffset.TryParse(status.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var wrote)
            && wrote >= began)
        {
            return wrote - began;
        }

        return TimeSpan.Zero;
    }

    private TimeSpan? DeadlineFor(string stage)
    {
        if (stageDeadlines is not null && stageDeadlines.TryGetValue(stage, out var overridden))
        {
            return overridden;
        }

        return DefaultStageDeadlines.TryGetValue(stage, out var standard) ? standard : null;
    }

    private static string Format(TimeSpan span) => $"{span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s";
}
