using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Views;

/// <summary>Hard Rule 8: constructor DI and nothing that branches. Everything the first-run wizard does lives in <see cref="WizardViewModel"/> and <see cref="SetupStepsViewModel"/>.</summary>
public sealed partial class WizardPage : Page
{
    public WizardViewModel ViewModel { get; }

    public WizardPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<WizardViewModel>();
        InitializeComponent();
    }
}
