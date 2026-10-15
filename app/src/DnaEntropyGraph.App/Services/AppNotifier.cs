using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The Windows notification for a finished run (<see cref="IDesktopNotifier"/>, issue #107), through the Windows App SDK's
/// AppNotificationManager (it works for an unpackaged app once <c>DesktopActivation.Register</c> has run). Shown only while the window
/// is not in front. Both the body and the "Open results" button carry <see cref="AppDeepLink.ForRun"/>, so a click opens that run
/// whether the app is running or was closed.
/// </summary>
public sealed class AppNotifier(IAppWindow window, IStringResourceProvider strings) : IDesktopNotifier
{
    public void Notify(string title, string body, string? jobId)
    {
        if (window.IsInForeground || !AppNotificationManager.IsSupported())
        {
            return;
        }

        var builder = new AppNotificationBuilder().AddText(title).AddText(body);
        if (jobId is not null)
        {
            _ = AppDeepLink.ForRun(jobId); // refuses an id that is not a plain job id
            builder.AddArgument(AppDeepLink.RunKey, jobId);
            builder.AddButton(new AppNotificationButton(strings.GetString("RunToast_OpenResults")).AddArgument(AppDeepLink.RunKey, jobId));
        }

        AppNotificationManager.Default.Show(builder.BuildNotification());
    }
}
