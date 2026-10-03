using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Views;

/// <summary>
/// Hard Rule 8: constructor DI and nothing that branches. The two drag handlers are single calls:
/// <see cref="DropPaths"/> decides whether a drag is acceptable and reads the dropped paths, and the
/// paths go straight to <see cref="NewRunViewModel.AddPathsCommand"/>.
/// </summary>
public sealed partial class NewRunPage : Page
{
    public NewRunViewModel ViewModel { get; }

    public NewRunPage()
    {
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services.GetRequiredService<NewRunViewModel>();
        InitializeComponent();
        DropZone.DragOver += (_, e) => DropPaths.AcceptFiles(e);
        DropZone.Drop += async (_, e) => await ViewModel.AddPathsCommand.ExecuteAsync(await DropPaths.ReadAsync(e));
    }
}
