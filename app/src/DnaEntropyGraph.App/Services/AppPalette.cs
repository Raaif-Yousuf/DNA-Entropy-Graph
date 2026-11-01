using DnaEntropyGraph.Core.Palettes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The palette the user chose (issue #195), for drawing code that is not built through DI (the chart and sparkline controls). Asks
/// <see cref="ITrackPaletteProvider"/> on every call; the provider caches the setting and forgets it when the choice changes, so nothing here caches a colour.
/// </summary>
internal static class AppPalette
{
    public static TrackPalette Current
        => (Application.Current as App)?.Services.GetService<ITrackPaletteProvider>()?.Current ?? TrackPalettes.Default;
}
