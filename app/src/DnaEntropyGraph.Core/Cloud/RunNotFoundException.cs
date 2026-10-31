namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830 review M5: a stop, delete or cancel of a run found no row for it in the history. The run's own project and Google account live in that row, so with no row the app
/// cannot say where its computer is and must not guess from the selected project. The run page shows a message (resw <c>RunAction_RunNotFound</c>) naming the Cloud page as the place to look.
/// The message carries only the job id (never a path, a project or an email).
/// </summary>
public sealed class RunNotFoundException(string jobId) : Exception($"No history row for run {jobId}.")
{
    public string JobId { get; } = jobId;
}
