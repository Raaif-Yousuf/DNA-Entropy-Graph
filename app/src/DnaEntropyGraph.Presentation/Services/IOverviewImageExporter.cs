using DnaEntropyGraph.Core.Tracks;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>Draws a contig's entropy overview to a PNG file (issue #118). Abstracted so a ViewModel stays free of the chart library (Hard Rule 8).</summary>
public interface IOverviewImageExporter
{
    /// <summary>Writes the picture to <paramref name="path"/>; false when it could not be written (never throws for a disk problem).</summary>
    bool Export(EntropyContig contig, string path);
}
