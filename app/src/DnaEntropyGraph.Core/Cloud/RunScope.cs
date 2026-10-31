using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830: the one place a run ROW becomes a <see cref="CloudScope"/>. Everything that acts on an existing run (the reconciler, a re-download, a delete, the run page's stop)
/// asks the row, never the current selection. A row with no recorded account (a legacy row whose project had none) acts as the current account.
/// </summary>
public static class RunScope
{
    public static CloudScope Of(RunRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CloudScope(row.ProjectId ?? string.Empty, row.AccountSub);
    }
}
