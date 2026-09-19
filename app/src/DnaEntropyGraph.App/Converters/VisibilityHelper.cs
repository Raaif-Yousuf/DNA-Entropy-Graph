using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App.Converters;

/// <summary>
/// A plain static function <c>x:Bind</c> can call directly
/// (<c>{x:Bind converters:VisibilityHelper.FromBool(...)}</c>), used
/// instead of an <c>IValueConverter</c> declared in a resource dictionary.
/// MEASURED 2026-09-19: a <c>Window.Resources</c> entry instantiating a
/// locally-defined <c>IValueConverter</c> crashed this Windows App SDK
/// version's XamlCompiler with no diagnostic output, reproduced in
/// isolation; this free function sidesteps the resource dictionary
/// entirely and builds clean. See docs/ui_conventions.md section 8 and
/// issue #423.
/// </summary>
public static class VisibilityHelper
{
    public static Visibility FromBool(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
