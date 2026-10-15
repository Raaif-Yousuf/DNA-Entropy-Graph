using CommunityToolkit.Mvvm.ComponentModel;
using DnaEntropyGraph.Core;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One checkbox in the New run page's "Files you get back" list: a catalog file, its label, and whether it is ticked (issue #103).</summary>
public sealed partial class OutputChoiceItem : ObservableObject
{
    private readonly Action<OutputChoiceItem> _changed;

    public OutputChoiceItem(OutputEntry entry, string label, bool isChecked, Action<OutputChoiceItem> changed)
    {
        Entry = entry;
        Label = label;
        _isChecked = isChecked;
        _changed = changed;
    }

    public OutputEntry Entry { get; }

    /// <summary>The worker's name for the file (the manifest's <c>outputs</c> entry).</summary>
    public string WireName => Entry.WireName;

    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _changed(this);
}
