namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Removes a run's row from the history (issue #101's "remove"). Deliberately separate from
/// <see cref="IRunRepository"/>: removing history is a user action on the Runs page, never part of the
/// run machinery, and it touches no output files and no cloud object (Hard Rule 14).
/// </summary>
public interface IRunHistoryRemover
{
    /// <summary>Deletes the row for <paramref name="jobId"/>. An unknown id is not an error.</summary>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
}
