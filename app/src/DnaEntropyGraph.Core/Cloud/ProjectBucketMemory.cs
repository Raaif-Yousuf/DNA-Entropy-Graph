using System.Data.Common;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// After a cloud run ends, remembers the results bucket it used under the run's OWN project (issue #260): the project the user has
/// selected by now may be a different one. Used by the engine for a run it started and by the reconciler for one it resumed.
/// A convenience: a store that cannot be written never turns a finished run into a failure.
/// </summary>
public static class ProjectBucketMemory
{
    public static async Task RememberAsync(IRunRepository runs, IProjectSettingsStore store, string jobId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            // A row with no project (a damaged or very old row) has nowhere to remember a bucket. The store rejects a blank key with an exception that is not in the list below,
            // and this runs AFTER a run ended (the engine's finally, the reconciler), where an escaping exception would mask the run's own outcome.
            System.Diagnostics.Trace.TraceWarning("bucket not remembered: the run row names no project");
            return;
        }

        try
        {
            var all = await runs.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
            var row = RunRowStore.LatestRecord(all, jobId);
            if (!string.IsNullOrWhiteSpace(row?.Bucket))
            {
                await store.RememberBucketAsync(projectId, row.Bucket, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbException or UnauthorizedAccessException)
        {
            // Nothing to do: the next run in this project remembers it again.
        }
    }
}
