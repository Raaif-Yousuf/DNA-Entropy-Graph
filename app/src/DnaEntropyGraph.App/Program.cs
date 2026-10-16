using DnaEntropyGraph.App.Startup;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace DnaEntropyGraph.App;

/// <summary>
/// The hand-written entry point (#513; the XAML-generated one is disabled by DISABLE_XAML_GENERATED_MAIN in the csproj).
/// <c>VelopackApp.Build().Run()</c> must be the first thing that runs: the installer launches the exe with install,
/// uninstall and update arguments and expects it to handle them and exit before any window exists, and <c>vpk pack</c>
/// refuses an app that does not call it. Guarded by Guards.Tests/VelopackEntryPointGuardTests.
/// </summary>
public static class Program
{
    /// <summary>Velopack's uninstall hook: runs in the uninstaller's process, before any app state exists. A method (not a lambda) so the entry-point guard's one-chain pattern matches.</summary>
    private static void OnBeforeUninstall(SemanticVersion version)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        DesktopActivation.UnregisterAll();

        // Issue #108: the Open with entries go with the app. Best effort: an uninstall must never fail over a registry key.
        try
        {
            new Core.FileAssociationService(new Services.CurrentUserClasses(), () => null).Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning($"open with entries could not be removed: {ex.GetType().Name}");
        }

        // Issue #264: the user's data is kept unless they say otherwise (one question, default No).
        _ = UninstallDataPrompt.Run();
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // Issue #107: on uninstall the notification registration goes too, so Windows keeps no activator for an exe that is gone.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(OnBeforeUninstall)
            .Run();

        // Issue #638: the data folder (--profile / DEG_DATA_DIR) is fixed before anything can touch app state.
        if (!StartupDataRoot.TryResolve(FakeCloudMode.Resolve(args)) || !FakeCloudMode.CheckProfile(StartupDataRoot.Current))
        {
            Environment.ExitCode = 2;
            return;
        }

        // Issue #842: a run history written by a newer app ends the launch with a plain message, before any window.
        if (!StartupDataRoot.TryOpenDatabase(StartupDataRoot.Current))
        {
            Environment.ExitCode = 2;
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Issue #107/#108: a second launch (a clicked notification, a double-clicked file) goes to the running app and ends here.
        if (SingleInstance.HandedToRunningInstance(StartupDataRoot.Current.Path))
        {
            return;
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
