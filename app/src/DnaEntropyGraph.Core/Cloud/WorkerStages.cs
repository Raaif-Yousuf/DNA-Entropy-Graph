namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The worker's <c>stage</c> values (docs/job_contract.md section 4, "Stage list, in order"). The support bundle reads this list so a run tag that
/// happens to be a stage word does not blank that word in the copied files; <c>WorkerStagesParityTests</c> keeps it equal to the contract and to
/// every stage literal in the worker source (Python keyword and dict forms, the <c>job_status</c> variable) and in <c>worker/vm/startup.sh</c>.
/// Code that means "this worker stage" (HeartbeatWatch, FakeGcp) uses these constants, never its own copy of the string.
/// </summary>
public static class WorkerStages
{
    public const string Queued = "queued";
    public const string Provisioning = "provisioning";
    public const string Booting = "booting";
    public const string Installing = "installing";
    public const string RestoringCache = "restoring-cache";
    public const string ModelLoading = "model-loading";
    public const string Running = "running";
    public const string Uploading = "uploading";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Idle = "idle";
    public const string Finalizing = "finalizing";

    public static IReadOnlyList<string> All { get; } =
    [
        Queued, Provisioning, Booting, Installing, RestoringCache, ModelLoading,
        Running, Uploading, Done, Failed, Cancelled, Idle, Finalizing,
    ];
}
