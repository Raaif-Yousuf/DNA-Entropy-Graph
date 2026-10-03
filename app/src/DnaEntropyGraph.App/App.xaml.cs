using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.App.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App;

/// <summary>
/// Hard Rule 8: this file holds <c>InitializeComponent()</c>, constructor
/// DI setup, and nothing else that branches. All the actual wiring logic
/// lives in <see cref="ServiceRegistration"/> (a plain class, not XAML
/// code-behind), which this constructor calls exactly once.
/// </summary>
public partial class App : Application
{
    /// <summary>The real production service provider. Never touched by Guards.Tests, which builds its own from the same <see cref="ServiceRegistration"/> call.</summary>
    public IServiceProvider Services { get; }

    private Window? _window;

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(StartupDataRoot.Current);
        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow(
            Services.GetRequiredService<Presentation.ViewModels.ShellViewModel>(),
            Services.GetRequiredService<Core.Abstractions.ISettingsStore>(),
            Services.GetRequiredService<Services.WindowPlacementService>(),
            Services.GetRequiredService<Services.WindowThemeApplier>());
        var navigationService = Services.GetRequiredService<NavigationService>();
        navigationService.Initialize(window.RootFrame);
        Startup.NavigationRoutes.RegisterAll(navigationService);
        Services.GetRequiredService<Services.WindowHandleProvider>().Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Services.GetRequiredService<DialogService>().Attach(window);
        window.Activate();
        _window = window;

        // Issue #59: reattach runs a killed app left behind. Not awaited: launch never waits on a run.
        _ = Startup.AppStartup.BeginAsync(Services);
    }
}
