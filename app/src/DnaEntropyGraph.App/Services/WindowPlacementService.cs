using System.Diagnostics;
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

    /// <summary>
    /// Windows parks a minimized window at -32000,-32000 with a title-bar-sized
    /// rect. MEASURED 2026-10-02 (#489): minimizing the real window saved
    /// "-32000,-32000,391,61". Any coordinate at or below this is that sentinel.
    /// </summary>
    public const int OffScreenSentinel = -30000;

    public const int MinWidth = 320;

    public const int MinHeight = 200;

    /// <summary>False for a minimized-window rect or a size too small to use.</summary>
    public bool IsPlausible => X > OffScreenSentinel && Y > OffScreenSentinel && Width >= MinWidth && Height >= MinHeight;

    /// <summary>
    /// <see cref="Default"/> is 1280x800 DIPs; AppWindow works in physical pixels,
    /// so multiply by the window's DPI scale (dpi / 96). A non-positive scale changes nothing.
    /// </summary>
    public WindowPlacement Scaled(double scale)
        => scale <= 0
            ? this
            : new WindowPlacement((int)Math.Round(X * scale), (int)Math.Round(Y * scale), (int)Math.Round(Width * scale), (int)Math.Round(Height * scale));

    /// <summary>Shrinks the size to the work area if larger, then moves the rect fully inside it.</summary>
    public WindowPlacement FitInto(int workX, int workY, int workWidth, int workHeight)
    {
        var width = Math.Min(Width, workWidth);
        var height = Math.Min(Height, workHeight);
        var x = Math.Max(workX, Math.Min(X, workX + workWidth - width));
        var y = Math.Max(workY, Math.Min(Y, workY + workHeight - height));
        return new WindowPlacement(x, y, width, height);
    }

    /// <summary>True when at least a 100x50 piece of the window overlaps the given work area, so the title bar can be grabbed.</summary>
    public bool IsReachableOn(int workX, int workY, int workWidth, int workHeight)
    {
        var overlapWidth = Math.Min(X + Width, workX + workWidth) - Math.Max(X, workX);
        var overlapHeight = Math.Min(Y + Height, workY + workHeight) - Math.Max(Y, workY);
        return overlapWidth >= 100 && overlapHeight >= 50;
    }

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

        var placement = new WindowPlacement(x, y, width, height);
        return placement.IsPlausible ? placement : null;
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

    public WindowPlacement Load() => TryLoad() ?? WindowPlacement.Default;

    /// <summary>The saved placement, or null on a fresh profile or when the saved value is unusable (so the caller can DPI-scale the default).</summary>
    public WindowPlacement? TryLoad()
    {
        try
        {
            return WindowPlacement.Parse(_settingsStore.GetString(PlacementKey));
        }
        catch (SettingsUnavailableException ex)
        {
            // #558: a locked settings file must not stop the window from opening; use the default.
            Trace.TraceWarning($"settings_unavailable while loading window placement: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Refuses an implausible (minimized or degenerate) placement so the last good one survives.</summary>
    public void Save(WindowPlacement placement)
    {
        if (!placement.IsPlausible)
        {
            return;
        }

        try
        {
            _settingsStore.SetString(PlacementKey, placement.Serialize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Runs on close (#558): losing one window position is fine, an exception on close is not.
            // The store bounds its own wait (about 300 ms), so a locked file cannot hang the close either.
            Trace.TraceWarning($"settings_unavailable while saving window placement: {ex.GetType().Name}");
        }
    }
}
