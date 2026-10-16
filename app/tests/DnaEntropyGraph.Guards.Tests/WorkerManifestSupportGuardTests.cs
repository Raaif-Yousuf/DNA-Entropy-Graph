using DnaEntropyGraph.Core.Contract;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #704: the app offers a manifest feature only while <see cref="WorkerManifestSupport"/> says the worker reads it, and
/// that flag must agree with the worker's own parser. Both directions fail: a flag raised before the worker reads the field
/// (the app would send a field that is silently ignored), and a worker that reads it while the flag is still down (the feature
/// stays dark for no reason). The message names the one edit that fixes it.
/// </summary>
public sealed class WorkerManifestSupportGuardTests
{
    private static string ManifestPy()
        => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "..", "worker", "src", "dna_entropy", "worker", "manifest.py"));

    private static bool Parsed(string wireName) => ManifestPy().Contains("\"" + wireName + "\"", StringComparison.Ordinal);

    [Fact]
    public void The_annotation_flag_matches_whether_the_worker_reads_inputs_annotation()
        => WorkerManifestSupport.Annotation.ShouldBe(
            Parsed("annotation"),
            "WorkerManifestSupport.Annotation and worker manifest.py disagree about inputs[].annotation: set the flag to what the parser does (Core/Contract/WorkerManifestSupport.cs).");

    [Fact]
    public void The_accession_flag_matches_whether_the_worker_reads_inputs_accession()
        => WorkerManifestSupport.Accession.ShouldBe(
            Parsed("accession"),
            "WorkerManifestSupport.Accession and worker manifest.py disagree about inputs[].accession: set the flag to what the parser does (Core/Contract/WorkerManifestSupport.cs).");

    [Fact]
    public void The_gc_and_codons_flag_matches_whether_the_worker_reads_analysis_gcWindow()
        => WorkerManifestSupport.GcAndCodons.ShouldBe(
            Parsed("gcWindow"),
            "WorkerManifestSupport.GcAndCodons and worker manifest.py disagree about analysis.gcWindow: set the flag to what the parser does (Core/Contract/WorkerManifestSupport.cs).");
}
