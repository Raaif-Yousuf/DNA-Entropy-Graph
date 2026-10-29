namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The worker's <c>stage</c> values (docs/job_contract.md section 4, "Stage list, in order"). The support bundle reads this list so a run tag that
/// happens to be a stage word does not blank that word in the copied files; <c>WorkerStagesParityTests</c> keeps it equal to the contract and to
/// every stage literal in the worker source.
/// </summary>
public static class WorkerStages
{
    public static IReadOnlyList<string> All { get; } =
    [
        "queued", "provisioning", "booting", "installing", "restoring-cache", "model-loading",
        "running", "uploading", "done", "failed", "cancelled", "idle", "finalizing",
    ];
}
