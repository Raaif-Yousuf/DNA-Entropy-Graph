using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Views;

/// <summary>Hard Rule 8: constructor DI and nothing that branches. The same singleton picker as Settings shows, so a choice made on one page is the choice on the other (issue #735).</summary>
public sealed partial class RunTargetPickerControl : UserControl
{
    public RunTargetPickerViewModel Picker { get; }

    public RunTargetPickerControl()
    {
        Picker = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<RunTargetPickerViewModel>();
        InitializeComponent();
    }
}
