using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
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

    private readonly ShellReadiness _readiness;

    public MainWindow(ShellViewModel shellViewModel, ISettingsStore settingsStore, WindowPlacementService windowPlacementService, WindowThemeApplier windowThemeApplier, ShellReadiness readiness, HighContrastThemeReapplier highContrastReapplier)
    {
        _readiness = readiness;
        ViewModel = shellViewModel;
        InitializeComponent();
        Title = ViewModel.WindowTitle;
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        SetTitleBar(AppTitleBar);
        NavSelectionBinder.Attach(RootNavigationView, ViewModel);
        // The window's content root, not the NavigationView: the title bar and the Mica backdrop sit outside the NavigationView (#639).
        windowThemeApplier.Attach(RootGrid, AppWindow);
        windowThemeApplier.Apply(ThemeApplier.ReadTheme(settingsStore) ?? "System");
        highContrastReapplier.Start();
        WindowPlacementApplier.Apply(this, windowPlacementService);
        // Ctrl+, (issue #110): the comma has no VirtualKey name, so XAML cannot spell it; 188 is VK_OEM_COMMA.
        SettingsNavItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = (Windows.System.VirtualKey)188, Modifiers = Windows.System.VirtualKeyModifiers.Control });
    }

    // Issue #490: Loaded fires after App.OnLaunched has handed RootFrame to NavigationService,
    // so selecting New run (the first menu item, ShellViewModel.InitialPageKey) here navigates the frame too.
    // Issue #108: only after that first navigation may an Open-with launch navigate (ShellReadiness), or it would be overwritten.
    // Issue #244: the item chosen is the one the user left on (their last page, or the page above their last run), read before the first navigation.
    private async void RootNavigationView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Bounded and never throwing inside the view model (a hung history falls back to New run after a short wait).
            await ViewModel.PrepareSessionAsync(CancellationToken.None);
            RootNavigationView.SelectedItem = NavSelection.Find(RootNavigationView, ViewModel.StartupNavKey);
        }
        finally
        {
            _readiness.MarkReady();
        }
        await ViewModel.RunStartupOffersAsync(CancellationToken.None);
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        => ViewModel.NavigateToCommand.Execute((args.SelectedItemContainer as NavigationViewItem)?.Tag);
}
