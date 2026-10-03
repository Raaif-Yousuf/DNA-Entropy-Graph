using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DnaEntropyGraph.App.Views;

/// <summary>
/// Hard Rule 8: constructor DI and nothing that branches. The navigation parameter is the run's
/// output folder (a string); all behaviour is in <see cref="ViewerViewModel"/> and <see cref="IgvViewerHost"/>.
/// </summary>
public sealed partial class ViewerPage : Page
{
    private readonly IgvViewerHost _host;

    public ViewerViewModel ViewModel { get; }

    public ViewerPage()
    {
        var services = ((App)Microsoft.UI.Xaml.Application.Current).Services;
        ViewModel = services.GetRequiredService<ViewerViewModel>();
        _host = services.GetRequiredService<IgvViewerHost>();
        InitializeComponent();
        Loaded += async (_, _) => await _host.AttachAsync(Viewer, ViewModel);
        Unloaded += (_, _) => _host.Detach();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.OpenRun(e.Parameter as string);
}
