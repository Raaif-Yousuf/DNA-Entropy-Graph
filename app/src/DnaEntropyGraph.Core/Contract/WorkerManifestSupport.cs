namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// Which manifest fields the worker a run starts on actually reads (issue #704). A field the worker does not parse is silently
/// ignored (docs/job_contract.md section 8), so a page that sent it would look like it worked and do nothing: the New run page
/// offers a feature only while its flag here is true. <c>Guards.Tests/WorkerManifestSupportGuardTests</c> fails when a flag and
/// <c>worker/src/dna_entropy/worker/manifest.py</c> disagree, so flipping a flag is the one edit that lands with the worker change.
/// </summary>
public static class WorkerManifestSupport
{
    /// <summary><c>inputs[].annotation</c>, a GFF3 paired with a FASTA (#135, #686).</summary>
    public static bool Annotation { get; } = true;

    /// <summary><c>inputs[].accession</c> and <c>accessionRange</c>, an NCBI accession fetched on the VM (#133, #684).</summary>
    public static bool Accession { get; } = true;

    /// <summary>The <c>gc</c> and <c>codons</c> outputs and <c>analysis.gcWindow</c> (#191, #688).</summary>
    public static bool GcAndCodons { get; } = true;
}
