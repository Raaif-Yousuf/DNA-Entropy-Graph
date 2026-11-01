using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Palettes;

/// <summary>What a colour is for. One role is one thing the user must be able to tell apart from the others on screen (issue #195).</summary>
public enum TrackRole
{
    /// <summary>The combined entropy track (and the overview line and the Runs sparkline).</summary>
    Entropy,

    /// <summary>The forward-pass entropy track.</summary>
    Forward,

    /// <summary>The reverse-complement-pass entropy track.</summary>
    Reverse,

    /// <summary>A smoothed entropy track.</summary>
    Smoothed,

    /// <summary>Seam markers and the drag selection: neutral black or white, so they differ from every track by lightness, not hue.</summary>
    Marker,
}

/// <summary>The five colours of one palette on one theme, as <c>#RRGGBB</c>.</summary>
public sealed record PaletteColors(string Entropy, string Forward, string Reverse, string Smoothed, string Marker)
{
    public string For(TrackRole role) => role switch
    {
        TrackRole.Entropy => Entropy,
        TrackRole.Forward => Forward,
        TrackRole.Reverse => Reverse,
        TrackRole.Smoothed => Smoothed,
        TrackRole.Marker => Marker,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The marker colour as a CSS <c>rgba()</c> (igv.js region colour).</summary>
    public string MarkerRgba(double alpha)
    {
        var (r, g, b) = PaletteValidator.ParseHex(Marker);
        return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {alpha})");
    }
}

/// <summary>One named palette with its light-theme and dark-theme colours.</summary>
public sealed record TrackPalette(string Id, PaletteColors Light, PaletteColors Dark)
{
    public PaletteColors For(bool dark) => dark ? Dark : Light;

    public string Hex(TrackRole role, bool dark) => For(dark).For(role);

    /// <summary>
    /// What the igv.js viewer is given, in either theme: the light colours. dark.css makes the page dark with <c>invert(0.9) hue-rotate(180deg)</c>
    /// over the whole viewer, so a colour set on a track is inverted too; that filter keeps hue and flips lightness, and
    /// <see cref="ViewerDarkFilter"/> reproduces it so the validator can check the colours as they end up on screen. The light colours were
    /// chosen to pass both ways (light directly, dark through the filter).
    /// </summary>
    public PaletteColors ForViewer() => Light;
}

/// <summary>
/// The single definition of every track colour (issue #195). The overview chart, the Runs sparkline, the PNG export and the
/// igv.js viewer (tracks and seam markers, through the bridge) all take their colours from here; nothing else spells a track colour.
/// <c>PaletteValidatorTests</c> checks each palette against the light and dark backgrounds and under colour-vision simulation.
/// </summary>
public static class TrackPalettes
{
    /// <summary>The settings key of the Appearance palette choice.</summary>
    public const string SettingKey = "TrackPalette";

    public const string DefaultId = "default";
    public const string HighContrastId = "high-contrast";

    /// <summary>
    /// Okabe and Ito (2008), "Color Universal Design" (the 8-colour set that stays distinguishable in every common colour blindness).
    /// The light theme takes the darker members (blue, vermillion, bluish green, reddish purple); the dark theme swaps blue for sky blue
    /// and vermillion for orange so each still holds 3:1 against the dark background.
    /// </summary>
    public static TrackPalette Default { get; } = new(
        DefaultId,
        new PaletteColors("#0072B2", "#D55E00", "#009E73", "#CC79A7", "#000000"),
        new PaletteColors("#56B4E9", "#E69F00", "#009E73", "#CC79A7", "#FFFFFF"));

    /// <summary>Hues kept apart as far as 7:1 contrast against the background allows; for low vision, bright sun and projectors.</summary>
    public static TrackPalette HighContrast { get; } = new(
        HighContrastId,
        new PaletteColors("#001155", "#992200", "#005566", "#6633AA", "#000000"),
        new PaletteColors("#77AAFF", "#FFD000", "#11EEAA", "#FF88CC", "#FFFFFF"));

    public static IReadOnlyList<TrackPalette> All { get; } = [Default, HighContrast];

    /// <summary>The palette saved under <paramref name="id"/>; anything missing or unknown (an old or edited settings file) is the default.</summary>
    public static TrackPalette Resolve(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;
}

/// <summary>
/// The palette the user chose. Drawing code asks for it on every draw (a Runs list asks once per row), so an implementation must make
/// that cheap; <see cref="Invalidate"/> says the choice may have changed (a save, Reset all settings, a settings import).
/// </summary>
public interface ITrackPaletteProvider
{
    TrackPalette Current { get; }

    /// <summary>Forget what was read; the next <see cref="Current"/> reads again. A provider that reads nothing has nothing to forget.</summary>
    void Invalidate()
    {
    }
}

/// <summary>
/// Reads <see cref="TrackPalettes.SettingKey"/> once and keeps the answer until <see cref="Invalidate"/> (the settings store takes a lock and
/// reads its file on every call, and a Runs list asks once per row). A locked settings file gives the default rather than a crash (#558) and
/// is not remembered, so the next ask tries the file again (a locked file is retried on every draw; acceptable, the lock clears in milliseconds). A hand edit of settings.json while the app runs shows after a restart.
/// </summary>
public sealed class SettingsTrackPaletteProvider(ISettingsStore settings) : ITrackPaletteProvider
{
    private readonly object _gate = new();
    private TrackPalette? _cached;

    public TrackPalette Current
    {
        get
        {
            lock (_gate)
            {
                if (_cached is not null)
                {
                    return _cached;
                }

                try
                {
                    return _cached = TrackPalettes.Resolve(settings.GetString(TrackPalettes.SettingKey));
                }
                catch (SettingsUnavailableException)
                {
                    return TrackPalettes.Default;
                }
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }
}
