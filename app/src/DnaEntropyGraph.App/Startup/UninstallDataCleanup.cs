namespace DnaEntropyGraph.App.Startup;

public enum UninstallDataOutcome
{
    /// <summary>The user kept their data (the default), or the question could not be asked.</summary>
    Kept,

    /// <summary>The user chose to remove it and it is gone.</summary>
    Removed,

    /// <summary>The user chose to remove it but something is still locked or took too long; nothing was thrown. A folder named <c>*.removing-*</c> may be left beside the data folder.</summary>
    Failed,

    /// <summary>There is no data folder.</summary>
    NothingToRemove,

    /// <summary>The data folder is the installer's own folder, so the uninstaller decides (no question is asked, nothing is deleted).</summary>
    SharedWithInstall,

    /// <summary>No one can answer (a silent or service-session uninstall): the data is kept and nothing is asked.</summary>
    NotInteractive,
}

/// <summary>
/// Issue #264: what uninstalling does with the user's data (<c>%LOCALAPPDATA%\DNAEntropyGraph</c>: history, settings, saved input copies, logs).
/// The default is to keep it (Hard Rule 14: nothing of the user's is deleted unless they ask), so a reinstall finds their history. The hook asks
/// once, default No, and only removes on Yes. The question, the delete and the paths are parameters so tests run without a window. Nothing here throws:
/// an uninstall must never fail over a question or a locked file.
/// <para>
/// The hook has a hard limit: MEASURED 2026-10-15 from Velopack 1.2.161's own docs (Velopack.xml, OnBeforeUninstallFastCallback): the process is
/// terminated if it has not finished within 30 seconds. So (1) a silent or non-interactive uninstall asks nothing and keeps the data;
/// (2) the question itself is time-boxed by the caller (a timeout counts as No); (3) a Yes first RENAMES the folder to a clearly named sibling
/// (<c>DNAEntropyGraph.removing-xxxxxxxx</c>, one atomic operation on the same volume) and only then deletes that, so being killed mid-delete leaves
/// a named leftover and never a half-deleted live data folder; (4) the delete waits at most <c>deleteBudget</c>, so this returns before the kill.
/// </para>
/// </summary>
public static class UninstallDataCleanup
{
    /// <summary>How long the delete may take before this gives up and returns, well inside Velopack's 30 second limit after the question's own time box.</summary>
    public static readonly TimeSpan DefaultDeleteBudget = TimeSpan.FromSeconds(12);

    public static UninstallDataOutcome Run(
        string dataFolder,
        string installRoot,
        Func<bool> userWantsRemoval,
        Action<string> deleteFolder,
        bool interactive = true,
        Action<string, string>? moveFolder = null,
        TimeSpan? deleteBudget = null)
    {
        if (!Directory.Exists(dataFolder))
        {
            return UninstallDataOutcome.NothingToRemove;
        }

        // Velopack deletes its own folder on uninstall. If the data lives in it, "keep" is not ours to give, and asking would promise what cannot be done.
        // A refusal, never a delete: whatever this returns here, nothing was touched.
        if (Overlaps(dataFolder, installRoot))
        {
            return UninstallDataOutcome.SharedWithInstall;
        }

        if (!interactive)
        {
            return UninstallDataOutcome.NotInteractive;
        }

        try
        {
            if (!userWantsRemoval())
            {
                return UninstallDataOutcome.Kept;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"uninstall question failed, keeping data: {ex.GetType().Name}");
            return UninstallDataOutcome.Kept;
        }

        var leftover = dataFolder.TrimEnd('\\', '/') + ".removing-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            (moveFolder ?? Directory.Move)(dataFolder, leftover);
        }
        catch (Exception ex)
        {
            // Nothing moved, nothing deleted: the data is exactly as it was.
            System.Diagnostics.Trace.TraceWarning($"data folder could not be set aside: {ex.GetType().Name}");
            return UninstallDataOutcome.Failed;
        }

        return DeleteWithin(leftover, deleteFolder, deleteBudget ?? DefaultDeleteBudget);
    }

    private static UninstallDataOutcome DeleteWithin(string leftover, Action<string> deleteFolder, TimeSpan budget)
    {
        var delete = Task.Run(() => deleteFolder(leftover));
        try
        {
            if (!delete.Wait(budget))
            {
                System.Diagnostics.Trace.TraceWarning("data folder delete is still running at the deadline; leaving the renamed folder");
                return UninstallDataOutcome.Failed;
            }

            return UninstallDataOutcome.Removed;
        }
        catch (AggregateException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"data folder could not be removed: {ex.GetBaseException().GetType().Name}");
            return UninstallDataOutcome.Failed;
        }
    }

    /// <summary>True when the two folders are the same folder, or one is inside the other, judged by where they really are (junctions and links followed, 8.3 names expanded; Windows paths are not case sensitive).</summary>
    public static bool Overlaps(string first, string second)
    {
        var a = Normalize(FinalPath.Resolve(first));
        var b = Normalize(FinalPath.Resolve(second));
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
}
