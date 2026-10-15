namespace DnaEntropyGraph.App.Startup;

public enum UninstallDataOutcome
{
    /// <summary>The user kept their data (the default), or the question could not be asked.</summary>
    Kept,

    /// <summary>The user chose to remove it and it is gone.</summary>
    Removed,

    /// <summary>The user chose to remove it but something is still locked; nothing was thrown.</summary>
    Failed,

    /// <summary>There is no data folder.</summary>
    NothingToRemove,

    /// <summary>The data folder is the installer's own folder, so the uninstaller decides (no question is asked).</summary>
    SharedWithInstall,
}

/// <summary>
/// Issue #264: what uninstalling does with the user's data (<c>%LOCALAPPDATA%\DNAEntropyGraph</c>: history, settings, saved input copies, logs).
/// The default is to keep it (Hard Rule 14: nothing of the user's is deleted unless they ask), so a reinstall finds their history. The hook asks
/// once, default No, and only removes on Yes. The question, the delete and the paths are parameters so tests run without a window. Nothing here throws:
/// an uninstall must never fail over a question or a locked file.
/// </summary>
public static class UninstallDataCleanup
{
    public static UninstallDataOutcome Run(string dataFolder, string installRoot, Func<bool> userWantsRemoval, Action<string> deleteFolder)
    {
        if (!Directory.Exists(dataFolder))
        {
            return UninstallDataOutcome.NothingToRemove;
        }

        // Velopack deletes its own folder on uninstall. If the data lives in it, "keep" is not ours to give, and asking would promise what cannot be done.
        if (Overlaps(dataFolder, installRoot))
        {
            return UninstallDataOutcome.SharedWithInstall;
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

        try
        {
            deleteFolder(dataFolder);
            return UninstallDataOutcome.Removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"data folder could not be removed: {ex.GetType().Name}");
            return UninstallDataOutcome.Failed;
        }
    }

    /// <summary>True when the two folders are the same folder, or one is inside the other (Windows paths are not case sensitive).</summary>
    public static bool Overlaps(string first, string second)
    {
        var a = Normalize(first);
        var b = Normalize(second);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
}
