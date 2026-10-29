using DnaEntropyGraph.Core.Tracks;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>The words printed on an exported overview picture, already localised: a title (run name and contig) and the two axis labels (issue #587).</summary>
public sealed record OverviewImageLabels(string Title, string XAxis, string YAxis);

/// <summary>Draws a contig's entropy overview to a PNG file (issues #118, #587). Abstracted so a ViewModel stays free of the chart library (Hard Rule 8).</summary>
public interface IOverviewImageExporter
{
    /// <summary>Writes the picture to <paramref name="path"/>; false when it could not be written (never throws for a disk problem).</summary>
    bool Export(EntropyContig contig, OverviewImageLabels labels, string path);
}
