namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The worker's <c>stage</c> values (docs/job_contract.md section 4, "Stage list, in order"). The support bundle reads this list so a run tag that
/// happens to be a stage word does not blank that word in the copied files. <c>WorkerStagesParityTests</c> enforces: (1) this list equals the
/// contract's list; (2) every stage literal the worker writes (Python keyword form, dict form, the <c>job_status</c> variable) and every
/// <c>status &lt;stage&gt;</c> call in <c>worker/vm/startup.sh</c> is in it; (3) no file under <c>app/src</c> spells a stage as a string (a stage-name
/// literal on a line that mentions a stage, or any literal compared with or assigned to a <c>Stage</c> member or <c>"stage"</c> JSON key, bare or
/// escaped): use these constants. It does NOT see single-quoted, positional or dynamically built stage strings.
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
