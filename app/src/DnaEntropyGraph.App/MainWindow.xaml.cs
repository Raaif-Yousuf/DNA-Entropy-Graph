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

    public MainWindow(ShellViewModel shellViewModel, ISettingsStore settingsStore, WindowPlacementService windowPlacementService, WindowThemeApplier windowThemeApplier, ShellReadiness readiness)
    {
        _readiness = readiness;
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
        // Ctrl+, (issue #110): the comma has no VirtualKey name, so XAML cannot spell it; 188 is VK_OEM_COMMA.
        SettingsNavItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = (Windows.System.VirtualKey)188, Modifiers = Windows.System.VirtualKeyModifiers.Control });
    }

    // Issue #490: Loaded fires after App.OnLaunched has handed RootFrame to NavigationService,
    // so selecting New run (the first menu item, ShellViewModel.InitialPageKey) here navigates the frame too.
    // Issue #108: only after that first navigation may an Open-with launch navigate (ShellReadiness), or it would be overwritten.
    private void RootNavigationView_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        _readiness.MarkReady();
        // Issue #143: the first-launch tour opens once layout has run, so the nav items it points at have a size; the pane layout is told first.
        ViewModel.OnNavigationDisplayModeChanged((int)RootNavigationView.DisplayMode);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = ViewModel.OnWindowLoadedAsync());
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        => ViewModel.NavigateToCommand.Execute((args.SelectedItemContainer as NavigationViewItem)?.Tag);

    // Issue #143: the tour tips point at nav items, which a narrow window hides.
    private void RootNavigationView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        => ViewModel.OnNavigationDisplayModeChanged((int)args.DisplayMode);

    private void TourTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        => ViewModel.OnTourTipClosed((int)args.Reason);
}
