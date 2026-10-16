namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// The one list of file extensions the New run page offers in its Add files picker and takes from a dropped
/// folder: what <see cref="SequenceSniffer"/> recognises (FASTA, GenBank) plus <c>.dna</c> (SnapGene, which the sniffer decides by magic bytes rather than extension) the GFF3 annotation extensions (a GFF3 is paired with a FASTA file, never run on its own) and <c>.txt</c> for a plain sequence file.
/// </summary>
public static class SequenceFileTypes
{
    public static IReadOnlyList<string> Extensions { get; } = [.. SequenceSniffer.KnownExtensions, ".dna", .. AnnotationFileTypes.Extensions, ".txt"];

    public static bool IsSequenceFile(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
