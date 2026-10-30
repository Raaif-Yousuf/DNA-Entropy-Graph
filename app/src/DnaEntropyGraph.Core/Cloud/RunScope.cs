using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830: the one place a run ROW becomes a <see cref="CloudScope"/>. Everything that acts on an existing run (the reconciler, a re-download, a delete, the run page's stop)
/// asks the row, never the current selection. A null account is the current account until the <c>Runs.AccountSub</c> column exists (#830 stage 3 replaces it here).
/// </summary>
public static class RunScope
{
    public static CloudScope Of(RunRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CloudScope(row.ProjectId ?? string.Empty, null); // #830 stage 3
    }
}
