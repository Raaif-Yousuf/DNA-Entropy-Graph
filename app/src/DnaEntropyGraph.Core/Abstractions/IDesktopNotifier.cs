namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// A Windows notification outside the app window (issue #107), for a run that finished while the user was elsewhere. The in-app
/// message bar is <see cref="IToastService"/>; this is the second channel beside it. Implemented in the App project, where the
/// Windows App SDK lives; it shows nothing while the app window is in the front (the in-app message already did) and carries
/// <see cref="AppDeepLink.ForRun"/> so a click opens that run's Results page, also when the app was closed.
/// Safe to call from any thread; it never throws at the caller's expense (a failure is the caller's to ignore).
/// </summary>
public interface IDesktopNotifier
{
    /// <param name="jobId">The run the notification opens, or null for a notification with no page behind it.</param>
    void Notify(string title, string body, string? jobId);
}

/// <summary>The app's main window as Presentation sees it (issue #107): enough to answer a notification click.</summary>
public interface IAppWindow
{
    /// <summary>True while the main window is the one in front (so a Windows notification would only repeat what the user can see).</summary>
    bool IsInForeground { get; }

    /// <summary>Restores the window if it is minimized and brings it to the front.</summary>
    void BringToFront();
}
