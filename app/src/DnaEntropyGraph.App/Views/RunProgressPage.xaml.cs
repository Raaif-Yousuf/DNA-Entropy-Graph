using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DnaEntropyGraph.App.Views;

/// <summary>
/// Hard Rule 8: constructor DI (resolved from the app's single
/// <c>IServiceProvider</c>, since WinUI's <c>Frame.Navigate</c> constructs
/// pages via their parameterless constructor - there is no DI hook into
/// that call) and nothing else that branches. <see cref="OnNavigatedTo"/>
/// is a single unconditional assignment, not a branch.
/// </summary>
public sealed partial class RunProgressPage : Page
{
    public RunProgressViewModel ViewModel { get; }

    public RunProgressPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<RunProgressViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.JobId = e.Parameter as string;
}
