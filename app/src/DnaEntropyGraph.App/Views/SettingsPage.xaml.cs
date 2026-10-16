using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Views;

/// <summary>Hard Rule 8: constructor DI and InitializeComponent. Every decision lives in <see cref="SettingsViewModel"/>.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public RunDefaultsSettingsViewModel RunDefaults { get; }
    public ExternalViewersSettingsViewModel ExternalViewers { get; }

    public FilesSettingsViewModel Files { get; }

    public NotificationsSettingsViewModel Notifications { get; }

    public FileAssociationSettingsViewModel FileAssociation { get; }

    public AdvancedSettingsViewModel Advanced { get; }

    public SpendCapSettingsViewModel SpendCap { get; }
    public CloudResultsSettingsViewModel CloudResults { get; }

    public UpdateViewModel Updates { get; }

    public SettingsPage()
    {
        CloudResults = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<CloudResultsSettingsViewModel>();
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<SettingsViewModel>();
        RunDefaults = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<RunDefaultsSettingsViewModel>();
        Files = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<FilesSettingsViewModel>();
        Notifications = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<NotificationsSettingsViewModel>();
        FileAssociation = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<FileAssociationSettingsViewModel>();
        Advanced = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<AdvancedSettingsViewModel>();
        SpendCap = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<SpendCapSettingsViewModel>();
        ExternalViewers = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<ExternalViewersSettingsViewModel>();
        Updates = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<UpdateViewModel>();
        InitializeComponent();
    }
}
