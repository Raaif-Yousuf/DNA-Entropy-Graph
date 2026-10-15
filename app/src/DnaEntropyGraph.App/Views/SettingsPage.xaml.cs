using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Views;

/// <summary>Hard Rule 8: constructor DI and InitializeComponent. Every decision lives in <see cref="SettingsViewModel"/>.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public RunDefaultsSettingsViewModel RunDefaults { get; }

    public FilesSettingsViewModel Files { get; }

    public NotificationsSettingsViewModel Notifications { get; }

    public AdvancedSettingsViewModel Advanced { get; }

    public SettingsPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<SettingsViewModel>();
        RunDefaults = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<RunDefaultsSettingsViewModel>();
        Files = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<FilesSettingsViewModel>();
        Notifications = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<NotificationsSettingsViewModel>();
        Advanced = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<AdvancedSettingsViewModel>();
        InitializeComponent();
    }
}
