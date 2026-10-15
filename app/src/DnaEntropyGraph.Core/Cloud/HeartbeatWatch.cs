using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What <see cref="HeartbeatWatch"/> decided: the run code, the diagnostic sentence, and whether the VM should be deleted rather than stopped.</summary>
internal sealed record HeartbeatVerdict(string Code, string Message, bool DeleteVm);

/// <summary>
/// The judgement that "RUNNING is not working" (CLAUDE.md Critical Pitfalls): the worker's heartbeat in <c>status.json</c> is
/// the only health signal. Fed what the waiter read on each poll, with the time since the wait began on the app's own
/// monotonic clock. The worker's <c>updatedAt</c> is never compared with the app's wall clock (the two machines can disagree
/// by minutes); only whether <c>heartbeatSeq</c>/<c>updatedAt</c> CHANGED between looks is used, so skew cannot fake or hide a death.
/// One instance per wait; holds that wait's observations.
/// </summary>
internal sealed class HeartbeatWatch(bool expectsGpu, TimeSpan firstHeartbeatTimeout, TimeSpan staleTimeout, IReadOnlyDictionary<string, TimeSpan>? stageDeadlines = null)
{
    /// <summary>
    /// How long one stage may last, by <c>status.json</c> <c>stage</c> (docs/job_contract.md section 5, #90). A stage with no entry
    /// (<c>running</c>: its length depends on the input; <c>idle</c>; the terminal ones) has no deadline here: the result limit and the
    /// heartbeat judge it. <c>provisioning</c> is the create wait and is not a worker stage. THEORY (unverified): these are the
    /// contract's numbers, not measured on a real VM.
    /// </summary>
    public static IReadOnlyDictionary<string, TimeSpan> DefaultStageDeadlines { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["booting"] = TimeSpan.FromMinutes(8),
        ["installing"] = TimeSpan.FromMinutes(15),
        ["restoring-cache"] = TimeSpan.FromMinutes(15),
        ["model-loading"] = TimeSpan.FromMinutes(15),
        ["uploading"] = TimeSpan.FromMinutes(10),
    };

    private string? _stage;
    private TimeSpan _stageSince;
    private bool _workerSeen;
    private bool _gpuVerified;
    private long? _lastSeq;
    private string? _lastUpdatedAt;
    private TimeSpan _lastChange;

    /// <summary>True when the caller should also read <c>progress.jsonl</c>: a GPU tier whose worker is up and whose GPU is not yet confirmed.</summary>
    public bool NeedsProgress => expectsGpu && _workerSeen && !_gpuVerified;

    /// <summary>Null while the worker looks healthy; otherwise why the run must end. <paramref name="statusJson"/>/<paramref name="progressJsonl"/> are null when not (yet) there.</summary>
    public HeartbeatVerdict? Observe(string? statusJson, string? progressJsonl, TimeSpan elapsed)
    {
        var status = statusJson is null ? null : WorkerStatusReader.TryParseStatus(statusJson);

        // The stage clock restarts whenever the stage NAME changes, whoever wrote it (the startup script writes booting and installing).
        if (status?.Stage is { Length: > 0 } stage && !string.Equals(stage, _stage, StringComparison.Ordinal))
        {
            _stage = stage;
            _stageSince = elapsed;
        }

        if (status is { WorkerOwned: true })
        {
            if (!_workerSeen || status.HeartbeatSeq != _lastSeq || status.UpdatedAt != _lastUpdatedAt)
            {
                _lastChange = elapsed;
            }

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

        // A worker whose heartbeat thread ticks while its main thread is stuck in one stage (#90, partly #522) is caught here: the
        // stage deadline does not care that the heartbeat moves.
        if (_stage is not null && DeadlineFor(_stage) is { } deadline && elapsed - _stageSince >= deadline)
        {
            return new HeartbeatVerdict(
                RunErrorCodes.WorkerStageStuck,
                $"The worker stayed in stage '{_stage}' for {Format(elapsed - _stageSince)}, past that stage's {Format(deadline)} deadline.",
                DeleteVm: false);
        }

        if (!_workerSeen)
        {
            return elapsed >= firstHeartbeatTimeout
                ? new HeartbeatVerdict(
                    RunErrorCodes.WorkerNoHeartbeat,
                    $"No worker heartbeat appeared in status.json within {Format(firstHeartbeatTimeout)}.",
                    DeleteVm: false)
                : null;
        }

        return elapsed - _lastChange >= staleTimeout
            ? new HeartbeatVerdict(
                RunErrorCodes.WorkerHeartbeatStale,
                $"The worker's heartbeat in status.json has not changed for {Format(staleTimeout)} (last heartbeatSeq {_lastSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}).",
                DeleteVm: false)
            : null;
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
