using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Re-applies the window theme when Windows high contrast is switched on or off while the app runs (issue #701): the caption-button
/// palette is chosen inside <see cref="IThemeApplier.Apply"/>, so only another Apply picks up the change. The source raises its event
/// from a background thread, so the apply goes through the dispatcher.
/// </summary>
public sealed class HighContrastThemeReapplier
{
    private readonly IHighContrastSource _source;
    private readonly IThemeApplier _applier;
    private readonly IDispatcher _dispatcher;
    private bool _started;

    public HighContrastThemeReapplier(IHighContrastSource source, IThemeApplier applier, IDispatcher dispatcher)
    {
        _source = source;
        _applier = applier;
        _dispatcher = dispatcher;
    }

    /// <summary>Starts listening; calling it again does nothing.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _source.Changed += (_, _) => _dispatcher.Enqueue(() => _applier.Apply(_applier.CurrentTheme ?? "System"));
    }
}
