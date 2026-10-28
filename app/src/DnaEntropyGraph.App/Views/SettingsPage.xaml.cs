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

    public ThisPcSettingsViewModel ThisPc { get; }

    public CloudResultsSettingsViewModel CloudResults { get; }

    public UpdateViewModel Updates { get; }

    public TourViewModel Tour { get; }

    public SettingsPage()
    {
        CloudResults = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<CloudResultsSettingsViewModel>();
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<SettingsViewModel>();
        RunDefaults = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<RunDefaultsSettingsViewModel>();
        Files = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<FilesSettingsViewModel>();
        Notifications = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<NotificationsSettingsViewModel>();
        FileAssociation = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<FileAssociationSettingsViewModel>();
        Advanced = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<AdvancedSettingsViewModel>();
        ThisPc = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<ThisPcSettingsViewModel>();
        ExternalViewers = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<ExternalViewersSettingsViewModel>();
        Updates = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<UpdateViewModel>();
        Tour = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<TourViewModel>();
        InitializeComponent();
    }

    /// <summary>Looks at this PC every time the page opens, so the health card is never blank and never stale.</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => _ = ThisPc.RefreshCommand.ExecuteAsync(null);
}
