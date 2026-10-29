namespace DnaEntropyGraph.App.Startup;

/// <summary>Process exit codes for a launch that ends before any window opens. The one place they are defined.</summary>
public static class StartupExitCodes
{
    public const int Ok = 0;

    /// <summary>The data folder (--profile, DEG_DATA_DIR, portable) is unusable, or the fake-cloud profile is missing.</summary>
    public const int DataFolderUnusable = 2;

    /// <summary>The run history was written by a newer version of the app.</summary>
    public const int DatabaseNewerThanApp = 3;

    /// <summary>The run history is locked by another copy of the app.</summary>
    public const int DatabaseInUse = 4;

    /// <summary>The run history could not be checked for any other reason.</summary>
    public const int DatabaseUnusable = 5;
}
