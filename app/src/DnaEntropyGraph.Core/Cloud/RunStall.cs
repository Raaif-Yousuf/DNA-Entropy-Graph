namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What a <see cref="RunNotice"/> says.</summary>
public enum RunNoticeKind
{
    /// <summary>A stage has used 75 percent of its deadline: "taking longer than usual". Soft: nothing has been decided.</summary>
    SlowStage,

    /// <summary>The heartbeat watch fired and the runner is asking the caller to choose (<see cref="StallDecision"/>).</summary>
    StallDetected,

    /// <summary>The worker recovered while the question was open: the question is withdrawn and the run goes on.</summary>
    StallCleared,

    /// <summary>Issue #92: the VM request is queued and Google has not found a graphics card yet. Says nothing is wrong; the run is waiting by choice.</summary>
    WaitingForGpu,

    /// <summary>Issue #92: the queued request ended (a card was found, it ran out of time, or it was cancelled): the waiting line is withdrawn.</summary>
    WaitingForGpuEnded,
}

/// <summary>
/// A progress event from the runner to whoever shows the run (issue #675). <see cref="Stage"/> is the worker's stage when known; <see cref="InStage"/> and
/// <see cref="Deadline"/> are set for <see cref="RunNoticeKind.SlowStage"/>; <see cref="Code"/> is the run error code a stall would end under
/// (<see cref="RunErrorCodes.WorkerStageStuck"/>, <see cref="RunErrorCodes.WorkerHeartbeatStale"/>, <see cref="RunErrorCodes.WorkerNoHeartbeat"/>).
/// </summary>
public sealed record RunNotice(string JobId, RunNoticeKind Kind, string? Stage = null, TimeSpan InStage = default, TimeSpan Deadline = default, string? Code = null);

/// <summary>What the caller can choose when a run looks stalled.</summary>
public enum StallChoice
{
    /// <summary>End the run and stop the VM (it can be started again).</summary>
    Stop,

    /// <summary>End the run and delete the VM.</summary>
    Delete,

    /// <summary>Do not end anything: look again after a fresh allowance. The run's own time limit still applies.</summary>
    KeepWaiting,

    /// <summary>No opinion: end the run the way the run itself chose (its after-task action), exactly as if nobody had been asked. What a production default answers when no UI is there to ask (<see cref="StallDecisions"/>).</summary>
    EndAsRunChose,
}

/// <summary>
/// What the heartbeat watch decided, handed to the caller so the person can choose. <paramref name="RunName"/> (the input's file name or accession) and
/// <paramref name="VmName"/> (the computer a Delete would remove) are there so the question can name what it is about (#740): two queued questions must differ.
/// </summary>
public sealed record StallPrompt(string JobId, string Code, string Message, string? Stage, TimeSpan Waited, string? RunName = null, string? VmName = null);

/// <summary>
/// Asked once when the heartbeat watch fires (a stage past its deadline, a heartbeat gone stale, or no first heartbeat). The runner keeps polling for
/// <c>result.json</c> while the question is open and cancels <paramref name="cancellationToken"/> when the question no longer applies (the worker
/// recovered, the result arrived, or the run ended). A task that throws is treated as no answer: the VM is ended the way the run chose, so a broken
/// handler can never leave a VM billing. A box with no GPU is never asked about: it is deleted at once.
/// </summary>
public delegate Task<StallChoice> StallDecision(StallPrompt prompt, CancellationToken cancellationToken);

/// <summary>The production defaults of the stall and notice hooks (issue #675 review): safe with no UI attached, replaced by the app lane's real ones.</summary>
public static class StallDecisions
{
    /// <summary>
    /// Answers at once with <see cref="StallChoice.EndAsRunChose"/>: with nobody to ask, a stall ends the way it did before the question existed (the VM is ended
    /// as the run chose and the run fails with its stuck code), never a VM left running because a question had no listener.
    /// </summary>
    public static Task<StallChoice> EndAsRunChose(StallPrompt prompt, CancellationToken cancellationToken) => Task.FromResult(StallChoice.EndAsRunChose);
}
