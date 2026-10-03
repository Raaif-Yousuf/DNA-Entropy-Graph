using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DnaEntropyGraph.App.Views;

/// <summary>
/// Hard Rule 8: constructor DI and one unconditional call. The navigation parameter is the run's job id (a string);
/// every decision lives in <see cref="ResultsViewModel"/>.
/// </summary>
public sealed partial class ResultsPage : Page
{
    public ResultsViewModel ViewModel { get; }

    public ResultsPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<ResultsViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.LoadAsync(e.Parameter as string);
}
