using System.Runtime.InteropServices;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #264: the uninstall hook's one question, "Also remove your history and settings?", default No. Runs in the uninstaller's process
/// (Velopack's before-uninstall hook), where there is no window, so it is a plain Windows message box. A failure to show it is a No.
/// </summary>
public static class UninstallDataPrompt
{
    private const uint MbYesNo = 0x4;
    private const uint MbIconQuestion = 0x20;
    private const uint MbDefButton2 = 0x100;
    private const uint MbTopmost = 0x40000;
    private const int IdYes = 6;

    /// <summary>The hook's whole job for the data folder: ask once and remove on Yes. Never throws.</summary>
    public static UninstallDataOutcome Run()
    {
        try
        {
            var installRoot = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/')) ?? AppContext.BaseDirectory;
            return UninstallDataCleanup.Run(AppDataRoot.Default().Path, installRoot, AskUser, path => Directory.Delete(path, recursive: true));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"uninstall data cleanup failed: {ex.GetType().Name}");
            return UninstallDataOutcome.Kept;
        }
    }

    private static bool AskUser()
    {
        var strings = new ReswStringResourceProvider();
        return MessageBoxW(IntPtr.Zero, strings.GetString("UninstallRemoveData_Body"), strings.GetString("UninstallRemoveData_Title"), MbYesNo | MbIconQuestion | MbDefButton2 | MbTopmost) == IdYes;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
