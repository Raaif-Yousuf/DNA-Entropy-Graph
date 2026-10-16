using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DnaEntropyGraph.App.Views;

/// <summary>
/// Hard Rule 8: constructor DI and one unconditional call. Every decision lives in
/// <see cref="CloudResourcesViewModel"/>; navigating here lists what the project holds.
/// </summary>
public sealed partial class CloudPage : Page
{
    public CloudResourcesViewModel ViewModel { get; }

    public CloudPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<CloudResourcesViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.RefreshCommand.ExecuteAsync(null);
}
