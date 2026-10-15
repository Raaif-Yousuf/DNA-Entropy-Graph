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
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // Issue #638: the data folder (--profile / DEG_DATA_DIR) is fixed before anything can touch app state.
        if (!StartupDataRoot.TryResolve(FakeCloudMode.Resolve(args)) || !FakeCloudMode.CheckProfile(StartupDataRoot.Current))
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
