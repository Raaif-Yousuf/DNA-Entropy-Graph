using System.Diagnostics;
using DnaEntropyGraph.Core.Cloud;

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
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No default browser registered, or Windows refused to start it.
            throw new AccountAuthException(AuthErrorCodes.BrowserUnavailable, ex.GetType().Name, ex);
        }

        return Task.CompletedTask;
    }
}
