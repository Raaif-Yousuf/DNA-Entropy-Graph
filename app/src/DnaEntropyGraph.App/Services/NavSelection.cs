using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Services;

/// <summary>Finds the navigation item to select for a page key (issue #244). Lives outside the code-behind because it branches (Hard Rule 8).</summary>
public static class NavSelection
{
    /// <summary>The menu or footer item whose Tag is <paramref name="key"/>, else the first menu item (New run); with a null key the current selection, untouched.</summary>
    public static object? Find(NavigationView view, string? key)
        => key is null ? view.SelectedItem : view.MenuItems.Concat(view.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, key, StringComparison.Ordinal))
            ?? view.MenuItems[0];
}
