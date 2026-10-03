using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Viewers;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Viewers;

/// <summary>
/// Issue #586: which of a run's files go to IGV and Geneious. The names are the worker's writer names
/// (<c>worker/src/dna_entropy/writers</c>): <c>&lt;name&gt;.fasta</c>, <c>&lt;name&gt;.gb</c>, <c>&lt;name&gt;.entropy.bedgraph</c>,
/// <c>&lt;name&gt;.genes.gff3</c>, <c>&lt;name&gt;.entropy.geneious.gff3</c>.
/// </summary>
public sealed class ExternalViewerFilesTests
{
    private static readonly RunOutputFile[] OneRun =
    [
        F("r/r.fasta"), F("r/r.gb"), F("r/r.entropy.bedgraph"), F("r/r.entropy.wig"), F("r/r.genes.gff3"),
        F("r/r.entropy.geneious.gff3"), F("r/r.summary.txt"), F("r/r.provenance.json"),
    ];

    private static RunOutputFile F(string relative) => new(relative, @"C:\out\" + relative.Replace('/', '\\'), 1);

    [Fact]
    public void Igv_gets_the_fasta_the_bedgraph_and_the_gene_gff3_and_nothing_else()
    {
        var files = ExternalViewerFiles.ForIgv(OneRun);

        files.Genome.ShouldBe(@"C:\out\r\r.fasta");
        files.BedGraphs.ShouldBe([@"C:\out\r\r.entropy.bedgraph"]);
        files.Gff3s.ShouldBe([@"C:\out\r\r.genes.gff3"]);
        files.HasTracks.ShouldBeTrue();
    }

    [Fact]
    public void An_absent_file_is_skipped_and_no_track_at_all_is_reported()
    {
        var onlyFasta = ExternalViewerFiles.ForIgv([F("r/r.fasta"), F("r/r.summary.txt")]);
        onlyFasta.Genome.ShouldNotBeNull();
        onlyFasta.HasTracks.ShouldBeFalse();

        var noGenome = ExternalViewerFiles.ForIgv([F("r/r.entropy.bedgraph")]);
        noGenome.Genome.ShouldBeNull();
        noGenome.HasTracks.ShouldBeTrue();
    }

    [Fact]
    public void Every_bedgraph_variant_is_kept_in_name_order()
    {
        var files = ExternalViewerFiles.ForIgv([F("r/r.fasta"), F("r/r.entropy.rev.bedgraph"), F("r/r.entropy.fwd.bedgraph")]);

        files.BedGraphs.Count.ShouldBe(2);
        files.BedGraphs[0].ShouldEndWith("r.entropy.fwd.bedgraph");
    }

    [Fact]
    public void In_a_multi_file_run_the_tracks_belong_to_the_folder_of_the_genome_that_is_loaded()
    {
        var files = ExternalViewerFiles.ForIgv(
            [F("a/a.fasta"), F("a/a.entropy.bedgraph"), F("b/b.fasta"), F("b/b.entropy.bedgraph")]);

        files.Genome.ShouldEndWith(@"a\a.fasta");
        files.BedGraphs.ShouldBe([@"C:\out\a\a.entropy.bedgraph"]);
    }

    [Fact]
    public void Matching_is_on_the_file_name_so_a_folder_called_dot_fasta_is_not_a_genome()
    {
        ExternalViewerFiles.ForIgv([F("x.fasta/notes.txt")]).Genome.ShouldBeNull();
    }

    [Fact]
    public void Geneious_gets_the_genbank_first_then_every_gff3()
    {
        var files = ExternalViewerFiles.ForGeneious(OneRun);

        files.Count.ShouldBe(3);
        files[0].ShouldEndWith("r.gb");
        files.ShouldContain(f => f.EndsWith("r.entropy.geneious.gff3", StringComparison.Ordinal));
        files.ShouldContain(f => f.EndsWith("r.genes.gff3", StringComparison.Ordinal));
    }

    [Fact]
    public void Geneious_with_nothing_to_open_gets_an_empty_list()
    {
        ExternalViewerFiles.ForGeneious([F("r/r.fasta"), F("r/r.entropy.bedgraph")]).ShouldBeEmpty();
    }
}
