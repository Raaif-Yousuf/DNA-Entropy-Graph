using System.Diagnostics;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>Opens Google's sign-in page in the user's browser. A seam so a test plays the browser without one.</summary>
public interface IBrowserLauncher
{
    Task LaunchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>The system default browser.</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task LaunchAsync(Uri url, CancellationToken cancellationToken)
    {
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}
