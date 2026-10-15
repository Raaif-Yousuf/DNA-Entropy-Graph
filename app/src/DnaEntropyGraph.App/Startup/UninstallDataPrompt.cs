using System.Runtime.InteropServices;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #264: the uninstall hook's one question, "Also remove your history and settings?", default No. Runs in the uninstaller's process
/// (Velopack's before-uninstall hook, which is terminated after 30 seconds, see <see cref="UninstallDataCleanup"/>), where there is no window,
/// so it is a plain Windows message box that closes itself after <see cref="PromptTimeoutMs"/> and counts as No. A failure to show it is a No, and
/// so is a session with no desktop (a silent uninstall run as a service or a scheduled task).
/// </summary>
public static class UninstallDataPrompt
{
    /// <summary>How long the question waits for an answer. Ten seconds leaves the rest of the 30 for the delete.</summary>
    public const uint PromptTimeoutMs = 10_000;

    /// <summary>Set to 1 by a deployment tool that runs the uninstaller unattended, to be sure nothing is asked.</summary>
    public const string SilentEnvironmentVariable = "DEG_SILENT_UNINSTALL";

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
            var installRoot = InstallRootOf(AppContext.BaseDirectory);
            return UninstallDataCleanup.Run(AppDataRoot.Default().Path, installRoot, AskUser, path => Directory.Delete(path, recursive: true), IsInteractive());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"uninstall data cleanup failed: {ex.GetType().Name}");
            return UninstallDataOutcome.Kept;
        }
    }

    /// <summary>Velopack runs the app from <c>&lt;root&gt;\current</c>, so the install root is the parent of the folder the exe is in.</summary>
    public static string InstallRootOf(string baseDirectory)
        => Path.GetDirectoryName(baseDirectory.TrimEnd('\\', '/')) ?? baseDirectory;

    private static bool IsInteractive()
        => Environment.UserInteractive && Environment.GetEnvironmentVariable(SilentEnvironmentVariable) != "1";

    private static bool AskUser()
    {
        var (title, body) = UninstallDataStrings.Resolve(LookUp);

        // MessageBoxTimeoutW is undocumented but stable since Windows XP; a missing entry point throws, which the cleanup counts as No.
        return MessageBoxTimeoutW(IntPtr.Zero, body, title, MbYesNo | MbIconQuestion | MbDefButton2 | MbTopmost, 0, PromptTimeoutMs) == IdYes;
    }

    private static string? LookUp(string key) => new ReswStringResourceProvider().GetString(key);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxTimeoutW")]
    private static extern int MessageBoxTimeoutW(IntPtr hWnd, string text, string caption, uint type, ushort languageId, uint milliseconds);
}
