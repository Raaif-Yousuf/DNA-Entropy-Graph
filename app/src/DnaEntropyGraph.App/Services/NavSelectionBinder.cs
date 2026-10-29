using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Keeps the NavigationView's selected item equal to <see cref="ShellViewModel.SelectedNavKey"/> (issues #499, #701, #807), so Back, a deep
/// link and a page's own navigation move the selection too. Lives outside the code-behind because it branches (Hard Rule 8). A key with no
/// nav item clears the selection; the resulting selection change reaches <see cref="ShellViewModel.NavigateToCommand"/> as a null key, which ignores it.
/// </summary>
public static class NavSelectionBinder
{
    public static void Attach(NavigationView view, ShellViewModel viewModel)
        => viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.SelectedNavKey))
            {
                Apply(view, viewModel.SelectedNavKey);
            }
        };

    /// <summary>The item whose Tag is <paramref name="key"/>, or null (nothing selected) for a null or unknown key.</summary>
    public static object? ItemFor(NavigationView view, string? key)
        => key is null ? null : view.MenuItems.Concat(view.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, key, StringComparison.Ordinal));

    private static void Apply(NavigationView view, string? key)
    {
        var item = ItemFor(view, key);
        if (!ReferenceEquals(view.SelectedItem, item))
        {
            view.SelectedItem = item;
        }
    }
}
