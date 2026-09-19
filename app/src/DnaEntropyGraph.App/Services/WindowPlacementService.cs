using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The main window's on-screen position and size, in the units
/// <c>AppWindow.MoveAndResize</c>/<c>RectInt32</c> use.
/// </summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height)
{
    /// <summary>Used on a fresh profile, or whenever the saved value cannot be trusted.</summary>
    public static WindowPlacement Default { get; } = new(100, 100, 1280, 800);

    public static WindowPlacement? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(',');
        if (parts.Length != 4)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var x)
            || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var width)
            || !int.TryParse(parts[3], out var height))
        {
            return null;
        }

        if (width <= 0 || height <= 0)
        {
            // A saved 0x0 (or negative) window would be invisible and
            // unrecoverable without editing settings.json by hand - refuse
            // it here rather than ever handing AppWindow a size like that.
            return null;
        }

        return new WindowPlacement(x, y, width, height);
    }

    public string Serialize() => $"{X},{Y},{Width},{Height}";
}

/// <summary>
/// Persists and restores the main window's position and size across
/// launches (issue #62) through <see cref="ISettingsStore"/> - the same
/// store <c>SettingsViewModel</c> already uses for Theme, so there is one
/// settings.json, not a second file for window state
/// (docs/architecture.md section 6).
/// </summary>
public sealed class WindowPlacementService
{
    private const string PlacementKey = "MainWindowPlacement";

    private readonly ISettingsStore _settingsStore;

    public WindowPlacementService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public WindowPlacement Load() => WindowPlacement.Parse(_settingsStore.GetString(PlacementKey)) ?? WindowPlacement.Default;

    public void Save(WindowPlacement placement) => _settingsStore.SetString(PlacementKey, placement.Serialize());
}
