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

    public MainWindow(ShellViewModel shellViewModel, ISettingsStore settingsStore, WindowPlacementService windowPlacementService, WindowThemeApplier windowThemeApplier)
    {
        ViewModel = shellViewModel;
        InitializeComponent();
        Title = ViewModel.WindowTitle;
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        SetTitleBar(AppTitleBar);
        // The window's content root, not the NavigationView: the title bar and the Mica backdrop sit outside the NavigationView (#639).
        windowThemeApplier.Attach(RootGrid, AppWindow);
        windowThemeApplier.Apply(ThemeApplier.ReadTheme(settingsStore) ?? "System");
        WindowPlacementApplier.Apply(this, windowPlacementService);
    }

    // Issue #490: Loaded fires after App.OnLaunched has handed RootFrame to NavigationService,
    // so selecting New run (the first menu item, ShellViewModel.InitialPageKey) here navigates the frame too.
    private void RootNavigationView_Loaded(object sender, RoutedEventArgs e)
        => RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        => ViewModel.NavigateToCommand.Execute((args.SelectedItemContainer as NavigationViewItem)?.Tag);
}
