using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #107 (and #108): one running app per data folder. A second launch (a clicked notification, a double-clicked file) hands its
/// arguments to the first and exits, so the first can bring its window forward and open the run or file. The key includes the data
/// folder so a sandbox profile (#638) is its own instance and never takes over the real one.
/// </summary>
public static class SingleInstance
{
    /// <summary>How long a second launch waits for the running copy to take the hand-over before it gives up and opens its own window.</summary>
    public static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True when another instance owns this data folder and this launch was handed over to it (the caller returns). False when this
    /// process is the main instance (its activations are queued in <see cref="ActivationInbox.Shared"/> from this moment, so a redirect
    /// that lands before the window exists is not lost), and also false when the hand-over failed or timed out: the caller then
    /// opens this launch's own window, which loses nothing (its own arguments are read at launch) and never leaves the user with no response.
    /// </summary>
    public static bool HandedToRunningInstance(string dataFolder)
    {
        AppInstance main;
        try
        {
            main = AppInstance.FindOrRegisterForKey(KeyFor(dataFolder));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Trace.TraceWarning($"single-instance registration failed: {ex.GetType().Name}");
            return false;
        }

        if (main.IsCurrent)
        {
            main.Activated += (_, args) => ActivationInbox.Shared.Post(DesktopActivation.ArgumentsOf(args));
            return false;
        }

        var arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        return TryRedirect(async () => await main.RedirectActivationToAsync(arguments), RedirectTimeout);
    }

    /// <summary>
    /// Runs <paramref name="redirect"/> off this thread and waits for it, at most <paramref name="timeout"/>. True only when it finished
    /// without an error. The wait pumps COM messages (<c>CoWaitForMultipleObjects</c>, the pattern of the Windows App SDK's
    /// single-instancing sample): the redirect is a cross-process COM call and a plain blocking wait on the STA thread can deadlock it.
    /// An exception or a timeout is reported as false, never thrown.
    /// </summary>
    public static bool TryRedirect(Func<Task> redirect, TimeSpan timeout, Func<ManualResetEvent, TimeSpan, bool>? wait = null)
    {
        using var done = new ManualResetEvent(false);
        var failed = new int[1];
        _ = Task.Run(async () =>
        {
            try
            {
                await redirect().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failed[0], 1);
                Trace.TraceWarning($"redirect to the running instance failed: {ex.GetType().Name}");
            }
            finally
            {
                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                    // The caller gave up on timeout and has left; nothing is waiting.
                }
            }
        });

        var signalled = (wait ?? PumpingWait)(done, timeout);
        return signalled && Volatile.Read(ref failed[0]) == 0;
    }

    private static bool PumpingWait(ManualResetEvent done, TimeSpan timeout)
    {
        var handles = new[] { done.SafeWaitHandle.DangerousGetHandle() };
        var hr = CoWaitForMultipleObjects(0, (uint)timeout.TotalMilliseconds, 1, handles, out var index);
        return hr == 0 && index == 0;
    }

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeoutMs, uint handleCount, IntPtr[] handles, out uint index);

    public static string KeyFor(string dataFolder)
        => "DnaEntropyGraph-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).TrimEnd('\\').ToUpperInvariant())))[..16];
}
