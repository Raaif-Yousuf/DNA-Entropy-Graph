using DnaEntropyGraph.Presentation.Services;
using Windows.UI.ViewManagement;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Windows high contrast state over <see cref="AccessibilitySettings"/> (issue #701). The WinRT subscription is made on the first
/// <see cref="Changed"/> subscriber, not in the constructor: it needs the app's UI thread, and DI builds this with none (MEASURED
/// 2026-10-16: a constructor subscription threw a COMException in Guards.Tests). The settings object is held in a field because its event stops with it.
/// </summary>
public sealed class AccessibilityHighContrastSource : IHighContrastSource
{
    private readonly AccessibilitySettings _settings = new();
    private bool _listening;
    private EventHandler? _changed;

    public bool IsHighContrast => _settings.HighContrast;

    public event EventHandler? Changed
    {
        add
        {
            _changed += value;
            if (!_listening)
            {
                _listening = true;
                _settings.HighContrastChanged += (_, _) => _changed?.Invoke(this, EventArgs.Empty);
            }
        }
        remove => _changed -= value;
    }
}
