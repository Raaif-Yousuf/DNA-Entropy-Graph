using DnaEntropyGraph.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #107: every way the app is told "open this" from outside, all ending in <see cref="ActivationRouter"/>: a click on a Windows
/// notification while the app runs, a launch from a notification (the app was closed), and a later launch handed over by
/// <see cref="SingleInstance"/>. Called once from App.OnLaunched after the window exists.
/// </summary>
internal static class DesktopActivation
{
    public static void Register(IServiceProvider services)
    {
        var router = services.GetRequiredService<ActivationRouter>();

        // The registration is what lets an unpackaged app show notifications and be launched by clicking one.
        AppNotificationManager.Default.NotificationInvoked += (_, args) => router.Handle(args.Argument);
        AppNotificationManager.Default.Register();

        // Later launches of the exe, redirected to this process.
        AppInstance.GetCurrent().Activated += (_, args) => router.Handle(ArgumentsOf(args));

        // This launch itself: the notification the user clicked while the app was closed.
        router.Handle(ArgumentsOf(AppInstance.GetCurrent().GetActivatedEventArgs()));
    }

    internal static string? ArgumentsOf(AppActivationArguments arguments) => arguments.Kind switch
    {
        ExtendedActivationKind.AppNotification => (arguments.Data as AppNotificationActivatedEventArgs)?.Argument,
        ExtendedActivationKind.Launch => (arguments.Data as ILaunchActivatedEventArgs)?.Arguments,
        _ => null,
    };
}
