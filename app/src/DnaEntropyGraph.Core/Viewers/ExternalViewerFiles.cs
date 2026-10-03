using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Viewers;

/// <summary>The desktop programs the Results page can hand a run to (issue #586).</summary>
public enum ExternalViewer
{
    Igv,
    Geneious,
}

/// <summary>What IGV is sent: the genome (null when the run wrote no FASTA), then entropy tracks, then gene features.</summary>
public sealed record IgvFiles(string? Genome, IReadOnlyList<string> BedGraphs, IReadOnlyList<string> Gff3s)
{
    /// <summary>False when there is nothing to draw on the genome, so the command says so instead of opening an empty IGV.</summary>
    public bool HasTracks => BedGraphs.Count > 0 || Gff3s.Count > 0;
}

/// <summary>
/// Picks which of a run's files go to which viewer, by the names the worker's writers give them
/// (<c>worker/src/dna_entropy/writers</c>, docs/science_and_formats.md section 5): <c>.fasta</c>, <c>.gb</c>,
/// <c>.entropy.bedgraph</c> (and <c>.entropy.&lt;variant&gt;.bedgraph</c>), <c>.genes.gff3</c>, <c>.entropy.geneious.gff3</c>.
/// A file that is absent is simply not in the result. Matching is on the file name, never the folder.
/// </summary>
public static class ExternalViewerFiles
{
    private const string GeneiousGff3Suffix = ".geneious.gff3";

    public static IgvFiles ForIgv(IReadOnlyList<RunOutputFile> files)
    {
        var genomeFile = files.Where(f => Has(f, ".fasta")).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

        // A multi-file run has one folder per input; tracks of another folder belong to another genome.
        var scope = genomeFile is null ? files : [.. files.Where(f => Folder(f) == Folder(genomeFile))];
        return new IgvFiles(
            genomeFile?.FullPath,
            Paths(scope, f => Has(f, ".bedgraph")),
            Paths(scope, f => Has(f, ".gff3") && !Has(f, GeneiousGff3Suffix)));
    }

    /// <summary>The GenBank files first, then every GFF3 (the gene features and the Geneious heatmap track).</summary>
    public static IReadOnlyList<string> ForGeneious(IReadOnlyList<RunOutputFile> files)
        => [.. Paths(files, f => Has(f, ".gb")), .. Paths(files, f => Has(f, ".gff3"))];

    private static List<string> Paths(IEnumerable<RunOutputFile> files, Func<RunOutputFile, bool> match)
        => [.. files.Where(match).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Select(f => f.FullPath)];

    private static bool Has(RunOutputFile file, string suffix)
        => FileName(file).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static string FileName(RunOutputFile file) => file.RelativePath[(file.RelativePath.LastIndexOf('/') + 1)..];

    private static string Folder(RunOutputFile file)
    {
        var slash = file.RelativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : file.RelativePath[..slash];
    }
}
