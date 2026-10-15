using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #107 (and #108): one running app per data folder. A second launch (a clicked notification, a double-clicked file) hands its
/// arguments to the first and exits, so the first can bring its window forward and open the run or file. The key includes the data
/// folder so a sandbox profile (#638) is its own instance and never takes over the real one.
/// </summary>
internal static class SingleInstance
{
    /// <summary>
    /// True when another instance owns this data folder and this launch was handed over to it (the caller returns). Called from
    /// Main on the STA thread, so the redirect runs on a worker thread and is waited for: the redirect needs the pump the wait would block.
    /// </summary>
    public static bool HandedToRunningInstance(string dataFolder)
    {
        var main = AppInstance.FindOrRegisterForKey(KeyFor(dataFolder));
        if (main.IsCurrent)
        {
            return false;
        }

        var arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        Task.Run(async () => await main.RedirectActivationToAsync(arguments)).GetAwaiter().GetResult();
        return true;
    }

    internal static string KeyFor(string dataFolder)
        => "DnaEntropyGraph-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).TrimEnd('\\').ToUpperInvariant())))[..16];
}
