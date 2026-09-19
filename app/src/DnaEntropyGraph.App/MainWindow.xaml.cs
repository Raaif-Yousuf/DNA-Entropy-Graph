using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DnaEntropyGraph.App;

/// <summary>
/// Hard Rule 8: constructor DI and nothing that branches. Every line here
/// is a single, unconditional call or assignment - the branch each one
/// needs (theme-string-to-ElementTheme, placement parsing, the
/// NavigationView selection-to-page-key mapping) lives in
/// <see cref="ThemeApplier"/>, <see cref="WindowPlacementApplier"/> and
/// <see cref="ShellViewModel.NavigateToCommand"/> respectively (issue #62).
/// RootFrame is exposed for App.xaml.cs to hand to NavigationService.
///
/// <see cref="ExtendsContentIntoTitleBar"/> and <see cref="Window.SystemBackdrop"/>
/// are set here rather than as XAML attributes/elements on purpose - see
/// MainWindow.xaml's own comment: setting
/// <c>ExtendsContentIntoTitleBar="True"</c> as a XAML attribute crashes
/// this Windows App SDK version's XamlCompiler with no diagnostic output.
/// </summary>
public sealed partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }

    public MainWindow(ShellViewModel shellViewModel, ISettingsStore settingsStore, WindowPlacementService windowPlacementService)
    {
        ViewModel = shellViewModel;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        SetTitleBar(AppTitleBar);
        ThemeApplier.Apply(RootNavigationView, settingsStore);
        WindowPlacementApplier.Apply(this, windowPlacementService);
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        => ViewModel.NavigateToCommand.Execute((args.SelectedItemContainer as NavigationViewItem)?.Tag);
}
