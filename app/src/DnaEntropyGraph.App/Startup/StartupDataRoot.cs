using System.Runtime.InteropServices;
using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Resolves the one data folder at process start (issue #638): <c>--profile &lt;dir&gt;</c>, then <c>DEG_DATA_DIR</c>, then the real
/// per-user folder (see <see cref="AppDataRoot"/>). <c>Program.Main</c> calls <see cref="TryResolve"/> once, before any window
/// exists; an unusable override ends the launch with a plain message naming one action, and never falls back to the real folder.
/// </summary>
public static class StartupDataRoot
{
    private const uint MbIconError = 0x10;

    /// <summary>The root every service was built over. Set once by <see cref="TryResolve"/>; the default folder until then.</summary>
    public static AppDataRoot Current { get; private set; } = AppDataRoot.Default();

    /// <summary>True when the root was resolved. False when the override is unusable; the message has then already been shown.</summary>
    public static bool TryResolve(IReadOnlyList<string> args)
    {
        try
        {
            Current = AppDataRoot.Resolve(args, Environment.GetEnvironmentVariable, Environment.CurrentDirectory);
            return true;
        }
        catch (DataFolderUnusableException ex)
        {
            Show(MessageFor(ex, new ReswStringResourceProvider()));
            return false;
        }
    }

    internal static string MessageFor(DataFolderUnusableException ex, Presentation.Services.IStringResourceProvider strings)
    {
        var template = ex.Reason switch
        {
            DataFolderProblem.MissingValue => strings.GetString("DataFolderUnusable_MissingValue"),
            DataFolderProblem.NotAFolder => strings.GetString("DataFolderUnusable_NotAFolder"),
            _ => strings.GetString("DataFolderUnusable_CannotCreate"),
        };
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, ex.Path);
    }

    private static void Show(string message)
    {
        Console.Error.WriteLine(message);
        _ = MessageBoxW(IntPtr.Zero, message, new ReswStringResourceProvider().GetString("AppDisplayName"), MbIconError);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
