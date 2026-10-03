using DnaEntropyGraph.Core.Abstractions;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Converters;

/// <summary>
/// A plain static function for <c>x:Bind</c> (same reason as <see cref="VisibilityHelper"/>: no resource-dictionary converter,
/// MainWindow.xaml's note) that maps the ViewModel's <see cref="ToastSeverity"/> to the InfoBar's own enum.
/// </summary>
public static class InfoBarSeverityHelper
{
    public static InfoBarSeverity From(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => InfoBarSeverity.Success,
        ToastSeverity.Warning => InfoBarSeverity.Warning,
        ToastSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };
}
