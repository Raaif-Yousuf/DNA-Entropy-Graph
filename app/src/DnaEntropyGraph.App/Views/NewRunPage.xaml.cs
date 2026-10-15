using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Views;

/// <summary>
/// Hard Rule 8: constructor DI and nothing that branches. The two drag handlers are single calls:
/// <see cref="DropPaths"/> decides whether a drag is acceptable and reads the dropped paths, and the
/// drop goes straight to <see cref="NewRunViewModel.AddDroppedCommand"/> (<c>Execute</c>, not <c>ExecuteAsync</c>: a failure there must not escape an async void handler).
/// </summary>
public sealed partial class NewRunPage : Page
{
    public NewRunViewModel ViewModel { get; }

    public NewRunPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<NewRunViewModel>();
        InitializeComponent();
        DropZone.DragOver += (_, e) => DropPaths.AcceptFiles(e);
        DropZone.Drop += async (_, e) => ViewModel.AddDroppedCommand.Execute(await DropPaths.ReadAsync(e));
    }

    /// <summary>Files handed over by "Open with" (issue #108) arrive as the navigation parameter; the ViewModel decides whether it is one.</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => ViewModel.ReceiveNavigationParameterCommand.Execute(e.Parameter);

    /// <summary>Leaving the page: later Open-with files must open a new New run page, not go to this one.</summary>
    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => ViewModel.DetachFromIntake();
}
