using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DnaEntropyGraph.App.Services;

/// <summary>What one Windows notification says: two lines, and (for a run) the run it opens plus the label of the button that opens it.</summary>
public sealed record NotificationContent(string Title, string Body, string? RunId, string? OpenLabel);

/// <summary>The Windows App SDK's AppNotificationManager behind a seam, so <see cref="AppNotifier"/> is tested without a desktop.</summary>
public interface INotificationSink
{
    bool IsSupported();

    void Show(NotificationContent content);
}

/// <summary>The real sink: builds the AppNotification and shows it through <see cref="AppNotificationManager"/>.</summary>
public sealed class WindowsNotificationSink : INotificationSink
{
    public bool IsSupported() => AppNotificationManager.IsSupported();

    public void Show(NotificationContent content)
    {
        var builder = new AppNotificationBuilder().AddText(content.Title).AddText(content.Body);
        if (content.RunId is not null)
        {
            builder.AddArgument(AppDeepLink.RunKey, content.RunId);
            builder.AddButton(new AppNotificationButton(content.OpenLabel ?? string.Empty).AddArgument(AppDeepLink.RunKey, content.RunId));
        }

        AppNotificationManager.Default.Show(builder.BuildNotification());
    }
}

/// <summary>
/// The Windows notification for a finished run (<see cref="IDesktopNotifier"/>, issue #107), through <see cref="INotificationSink"/>
/// (it works for an unpackaged app once <c>DesktopActivation.Register</c> has run). Shown only while the window
/// is not in front. Both the body and the "Open results" button carry <see cref="AppDeepLink.ForRun"/>, so a click opens that run
/// whether the app is running or was closed.
/// </summary>
public sealed class AppNotifier(IAppWindow window, IStringResourceProvider strings, INotificationSink sink) : IDesktopNotifier
{
    public void Notify(string title, string body, string? jobId)
    {
        if (jobId is not null)
        {
            _ = AppDeepLink.ForRun(jobId); // refuses an id that is not a plain job id
        }

        if (window.IsInForeground || !sink.IsSupported())
        {
            return;
        }

        sink.Show(new NotificationContent(title, body, jobId, jobId is null ? null : strings.GetString("RunToast_OpenResults")));
    }
}
