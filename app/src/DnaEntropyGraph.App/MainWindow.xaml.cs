using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App;

/// <summary>Hard Rule 8: InitializeComponent() and nothing else that branches. RootFrame is exposed for App.xaml.cs to hand to NavigationService.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
