using DnaEntropyGraph.Core.Abstractions;
using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Maps the "Theme" setting (<c>SettingsViewModel</c>'s own "Light" / "Dark"
/// / "System" strings) to a real <see cref="ElementTheme"/> and applies it
/// to the shell's root element at startup (issue #62's "ElementTheme
/// applied from settings at startup (default System)"). Pulled out of
/// MainWindow.xaml.cs specifically so the branch lives somewhere
/// unit-testable (Hard Rule 8 forbids it in code-behind) - see
/// <c>ThemeApplierTests.Resolve_maps_the_saved_setting_to_the_right_ElementTheme</c>.
/// </summary>
public static class ThemeApplier
{
    public static ElementTheme Resolve(string? theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void Apply(FrameworkElement root, ISettingsStore settingsStore) => root.RequestedTheme = Resolve(settingsStore.GetString("Theme"));
}
