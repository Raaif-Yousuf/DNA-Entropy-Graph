namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Which result files the Results page may hand to Windows with "Open" (issue #102): an allowlist of the file types the worker
/// writes (<c>worker/src/dna_entropy/writers</c>, docs/science_and_formats.md section 5), matched on the final extension only,
/// so <c>x.wig.exe</c> is an .exe. The output folder is the user's and anything can be dropped into it; opening a program or
/// script from a button labelled Open would run it, so everything else is refused. Show in folder still works for it.
/// </summary>
public static class ShellOpenPolicy
{
    /// <summary>Final extensions of every file the worker writes: sequence, GenBank, tracks, feature files, tables, summary, provenance, probabilities.</summary>
    public static IReadOnlySet<string> AllowedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".fasta", ".gb", ".bedgraph", ".wig", ".gff3", ".bed", ".tsv", ".csv", ".txt", ".json", ".gz", ".npy",
    };

    public static bool MayOpen(string path) => AllowedExtensions.Contains(Path.GetExtension(path));
}
