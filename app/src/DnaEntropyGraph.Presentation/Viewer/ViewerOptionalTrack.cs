using CommunityToolkit.Mvvm.ComponentModel;

namespace DnaEntropyGraph.Presentation.Viewer;

/// <summary>
/// One optional track of the embedded viewer that the user switches on and off (issue #552): a smoothed entropy track.
/// <see cref="Name"/> is the igv.js track name, which is also how the page finds the track to remove. Off at first, so the raw
/// entropy track stays the one drawn.
/// </summary>
public sealed partial class ViewerOptionalTrack : ObservableObject
{
    private readonly Action<ViewerOptionalTrack> _changed;

    [ObservableProperty]
    private bool _isOn;

    public ViewerOptionalTrack(int window, string format, string url, string label, string name, Action<ViewerOptionalTrack> changed)
    {
        Window = window;
        Format = format;
        Url = url;
        Label = label;
        Name = name;
        _changed = changed;
    }

    public int Window { get; }

    // Plumbing for the igv.js track config (ViewerViewModel); the user never reads a file format, so it is not a display property.
    internal string Format { get; }

    public string Url { get; }

    /// <summary>The words beside the switch.</summary>
    public string Label { get; }

    public string Name { get; }

    partial void OnIsOnChanged(bool value) => _changed(this);
}
