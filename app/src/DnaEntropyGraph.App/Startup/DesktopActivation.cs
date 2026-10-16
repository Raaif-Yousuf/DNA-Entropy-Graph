using System.Diagnostics;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #107: every way the app is told "open this" from outside, all ending in <see cref="ActivationRouter"/>: a click on a Windows
/// notification while the app runs, a launch from a notification (the app was closed), and a later launch handed over by
/// <see cref="SingleInstance"/> (queued in <see cref="ActivationInbox"/> until this runs). Called once from App.OnLaunched after the window exists.
/// </summary>
public static class DesktopActivation
{
    public static void Register(IServiceProvider services)
    {
        var router = services.GetRequiredService<ActivationRouter>();

        // The registration is what lets an unpackaged app show notifications and be launched by clicking one.
        AppNotificationManager.Default.NotificationInvoked += (_, args) => router.Handle(args.Argument);
        AppNotificationManager.Default.Register();

        // Redirected launches that already arrived (and every later one) reach the router.
        ActivationInbox.Shared.Attach(arguments => router.Handle(arguments));

        // This launch itself: the notification the user clicked while the app was closed.
        router.Handle(ArgumentsOf(AppInstance.GetCurrent().GetActivatedEventArgs()));
    }

    /// <summary>Removes this app's notification registration (on exit, and from the Velopack uninstall hook), so Windows keeps no stale activator. Never throws.</summary>
    public static void Unregister()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"notification unregister failed: {ex.GetType().Name}");
        }
    }

    /// <summary>The uninstall form: removes every registration of this app, including one a crashed run left. Never throws.</summary>
    public static void UnregisterAll()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"notification unregister failed: {ex.GetType().Name}");
        }
    }

    public static string? ArgumentsOf(AppActivationArguments arguments) => ArgumentsFrom(
        arguments.Kind,
        () => (arguments.Data as AppNotificationActivatedEventArgs)?.Argument,
        () => (arguments.Data as ILaunchActivatedEventArgs)?.Arguments);

    /// <summary>Picks the text by activation kind; the sources are read only for the kind that needs them.</summary>
    public static string? ArgumentsFrom(ExtendedActivationKind kind, Func<string?> notification, Func<string?> launch) => kind switch
    {
        ExtendedActivationKind.AppNotification => notification(),
        ExtendedActivationKind.Launch => launch(),
        _ => null,
    };
}
