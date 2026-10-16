using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core;

/// <summary>Which group of inputs a choice is made for: a GenBank file, or a FASTA file or pasted sequence (issue #103).</summary>
[Flags]
public enum OutputInputKinds
{
    None = 0,
    GenBank = 1,
    Sequence = 2,
    Both = GenBank | Sequence,
}

/// <summary>One file a run can return: its flag, the worker's name for it, its label, where it is offered, and what it needs.</summary>
/// <param name="Kind">The one flag bit this file owns.</param>
/// <param name="WireName">The name in the manifest's <c>outputs</c> array (job_contract.md section 3).</param>
/// <param name="LabelKey">The <c>Resources.resw</c> key of the checkbox label.</param>
/// <param name="Offered">The input groups the New run page offers this file for.</param>
/// <param name="DefaultOn">Whether the box starts checked when nothing has been remembered.</param>
/// <param name="NeedsGenes">True for a file that exists only when the run has genes; the manifest never asks for it when gene finding is off.</param>
public sealed record OutputEntry(OutputFileKinds Kind, string WireName, string LabelKey, OutputInputKinds Offered, bool DefaultOn, bool NeedsGenes);

/// <summary>
/// The one table of the output files a run can ask the worker for (issue #103). The New run page's checkboxes, the
/// remembered choice and the manifest's <c>outputs</c> array are all read from it, so a file cannot be offered without
/// being sent, or sent without being offered. Its names are the worker's vocabulary
/// (<c>docs/job_contract.md</c> section 3, the <c>outputs</c> row; <c>OutputCatalogTests</c> pins the two together
/// and <c>worker/tests/test_output_vocabulary.py</c> pins the worker to the same row). Order is the contract's order.
/// </summary>
public static class OutputCatalog
{
    // The worker writes every file for a GenBank input and for a FASTA or pasted one (pipeline.py gates each writer on
    // its own include_* flag on both paths), so every file is offered to both groups today; the column exists so a file
    // that only one kind can have is one edit here, not a branch in the page.
    public static IReadOnlyList<OutputEntry> Entries { get; } =
    [
        new(OutputFileKinds.GenBank, "genbank", "OutputChoice_genbank", OutputInputKinds.Both, DefaultOn: true, NeedsGenes: false),
        new(OutputFileKinds.Fasta, "fasta", "OutputChoice_fasta", OutputInputKinds.Both, true, false),
        new(OutputFileKinds.BedGraph, "bedgraph", "OutputChoice_bedgraph", OutputInputKinds.Both, true, false),
        new(OutputFileKinds.Wig, "wig", "OutputChoice_wig", OutputInputKinds.Both, false, false),
        new(OutputFileKinds.GeneiousGff3, "geneious_gff3", "OutputChoice_geneious_gff3", OutputInputKinds.Both, true, false),
        new(OutputFileKinds.GenesGff3, "genes_gff3", "OutputChoice_genes_gff3", OutputInputKinds.Both, true, true),
        new(OutputFileKinds.Smoothed, "smoothed", "OutputChoice_smoothed", OutputInputKinds.Both, true, false),
        new(OutputFileKinds.GeneSummary, "gene_summary", "OutputChoice_gene_summary", OutputInputKinds.Both, true, true),
        new(OutputFileKinds.Regions, "regions", "OutputChoice_regions", OutputInputKinds.Both, true, false),
        new(OutputFileKinds.Stats, "stats", "OutputChoice_stats", OutputInputKinds.Both, true, false),
        new(OutputFileKinds.EntropyTsv, "tsv", "OutputChoice_tsv", OutputInputKinds.Both, true, false),
        new(OutputFileKinds.Probs, "probs", "OutputChoice_probs", OutputInputKinds.Both, false, false),
        new(OutputFileKinds.ProbsNpy, "probs_npy", "OutputChoice_probs_npy", OutputInputKinds.Both, false, false),
    ];

    /// <summary>Every flag the catalog owns.</summary>
    public static OutputFileKinds Everything { get; } = Entries.Aggregate(OutputFileKinds.None, (all, e) => all | e.Kind);

    /// <summary>The boxes that start checked: every file except WIG (the worker writes one track format per run, so WIG is the alternative to the bedGraph, not an addition) and the large probability matrix.</summary>
    public static OutputFileKinds Defaults { get; } = Entries.Where(e => e.DefaultOn).Aggregate(OutputFileKinds.None, (all, e) => all | e.Kind);

    /// <summary>The worker writes ONE track format per run, so the bedGraph and WIG tracks are alternatives: both ticked keeps the bedGraph (the manifest builder applies the same rule).</summary>
    public static OutputFileKinds Normalize(OutputFileKinds kinds) => kinds.HasFlag(OutputFileKinds.BedGraph) ? kinds & ~OutputFileKinds.Wig : kinds;

    /// <summary>The group an input kind's choice belongs to: GenBank, or sequence (FASTA and pasted text).</summary>
    public static OutputInputKinds GroupFor(InputKind kind) => kind is InputKind.GenBank or InputKind.Embl or InputKind.SnapGene ? OutputInputKinds.GenBank : OutputInputKinds.Sequence;

    /// <summary>The files offered for a group, in the contract's order.</summary>
    public static IReadOnlyList<OutputEntry> For(OutputInputKinds group) => Entries.Where(e => (e.Offered & group) != 0).ToList();

    /// <summary>The worker's names for the chosen flags, in the contract's order.</summary>
    public static IReadOnlyList<string> ToWireNames(OutputFileKinds kinds) => Entries.Where(e => kinds.HasFlag(e.Kind)).Select(e => e.WireName).ToList();

    /// <summary>The flags for the worker's names; a name the catalog does not know is ignored.</summary>
    public static OutputFileKinds FromWireNames(IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        return Entries.Where(e => wanted.Contains(e.WireName)).Aggregate(OutputFileKinds.None, (all, e) => all | e.Kind);
    }
}
