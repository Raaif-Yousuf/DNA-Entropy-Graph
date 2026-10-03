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

    public static void Apply(FrameworkElement root, ISettingsStore settingsStore) => root.RequestedTheme = Resolve(ReadTheme(settingsStore));

    /// <summary>The saved theme, or null when the settings file is locked (#558): a launch never fails over the theme.</summary>
    public static string? ReadTheme(ISettingsStore settingsStore)
    {
        try
        {
            return settingsStore.GetString("Theme");
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while reading theme: {ex.GetType().Name}");
            return null;
        }
    }
}
