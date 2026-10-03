using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DnaEntropyGraph.App.Views;

/// <summary>
/// Hard Rule 8: constructor DI and one unconditional call. Every decision lives in
/// <see cref="HistoryViewModel"/>; navigating here loads the history.
/// </summary>
public sealed partial class RunsPage : Page
{
    public HistoryViewModel ViewModel { get; }

    public RunsPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<HistoryViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.RefreshCommand.ExecuteAsync(null);
}
