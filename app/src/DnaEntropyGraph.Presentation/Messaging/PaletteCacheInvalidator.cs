using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Palettes;

namespace DnaEntropyGraph.Presentation.Messaging;

/// <summary>
/// Makes the palette provider forget its cached choice when Reset all settings or a settings import changes the file under it
/// (both send <see cref="SettingsResetMessage"/>). Attached once at startup to the singleton provider, so it does not depend on the
/// Settings page (and its view model) still being alive.
/// </summary>
public static class PaletteCacheInvalidator
{
    public static void Attach(IMessenger messenger, ITrackPaletteProvider provider)
        => messenger.Register<ITrackPaletteProvider, SettingsResetMessage>(provider, static (p, _) => p.Invalidate());
}
