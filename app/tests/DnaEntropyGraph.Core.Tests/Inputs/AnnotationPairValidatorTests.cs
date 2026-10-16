using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #686 (Hard Rule 2): the same rules as the worker's <c>readers/gff3.py</c> and its FASTA + GFF3 pairing in
/// <c>readers/input.py</c> (branch feat/135-fasta-gff3-pair-r2), run on this PC before a cloud resource exists.
/// </summary>
public sealed class AnnotationPairValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-gff-" + Guid.NewGuid().ToString("N"));

    public AnnotationPairValidatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static string Seq(int length) => string.Concat(Enumerable.Repeat("ACGT", (length / 4) + 1))[..length];

    private static string Row(string seqid, string type, long begin, long end, string attrs = "ID=g1", string strand = "+")
        => string.Join('\t', seqid, "test", type, begin, end, ".", strand, ".", attrs) + "\n";

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private AnnotationCheckResult Check(string fasta, string gff, Topology topology = Topology.Auto)
        => AnnotationPairValidator.Check(Write("a.fasta", fasta), Write("a.gff3", gff), topology);

    private static readonly string Fasta100 = ">chr1 some description\n" + Seq(100) + "\n";

    [Fact]
    public void Extensions_gff3_and_gff_are_annotation_files_in_any_case()
    {
        AnnotationFileTypes.IsAnnotationFile("x.gff3").ShouldBeTrue();
        AnnotationFileTypes.IsAnnotationFile("x.GFF").ShouldBeTrue();
        AnnotationFileTypes.IsAnnotationFile("x.fasta").ShouldBeFalse();
        SequenceFileTypes.IsSequenceFile("x.gff3").ShouldBeTrue(); // the picker and a dropped folder take it
    }

    [Fact]
    public void A_matching_pair_counts_its_genes()
    {
        var gff = "##gff-version 3\n" + Row("chr1", "gene", 1, 30, "ID=a") + Row("chr1", "gene", 40, 90, "ID=b") + Row("chr1", "CDS", 1, 30, "ID=c");

        var result = Check(Fasta100, gff);

        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
        result.GeneCount.ShouldBe(2); // gene features are used as-is; CDS only when a sequence has no gene row
        result.NoticeCodes.ShouldBeEmpty();
    }

    [Fact]
    public void A_sequence_with_only_CDS_rows_uses_them()
        => Check(Fasta100, Row("chr1", "CDS", 1, 30, "ID=a") + Row("chr1", "CDS", 40, 60, "ID=b")).GeneCount.ShouldBe(2);

    [Fact]
    public void Rows_sharing_an_ID_are_one_feature()
        => Check(Fasta100, Row("chr1", "gene", 1, 10, "ID=x") + Row("chr1", "gene", 20, 30, "ID=x")).GeneCount.ShouldBe(1);

    [Fact]
    public void The_FASTA_name_is_the_header_up_to_the_first_space()
        => Check(">chr1\tdesc\n" + Seq(60) + "\n", Row("chr1", "gene", 1, 30)).IsValid.ShouldBeTrue();

    [Fact]
    public void A_percent_encoded_seqid_is_decoded_like_the_worker_does()
    {
        // GFF3 requires column 1 percent-encoded: "my%20contig" names the FASTA record "my contig"... which a header
        // split on whitespace can never carry, so only a name without a space can match; %7C is "|".
        var fasta = ">gi|123|ref\n" + Seq(60) + "\n";

        Check(fasta, Row("gi%7C123%7Cref", "gene", 1, 30)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_percent_encoded_multibyte_seqid_is_decoded_as_UTF8()
    {
        var fasta = ">café\n" + Seq(60) + "\n";

        Check(fasta, Row("caf%C3%A9", "gene", 1, 30)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void No_seqid_matching_any_FASTA_name_is_refused_with_the_two_possible_causes_left_to_the_copy()
    {
        var result = Check(Fasta100, Row("other", "gene", 1, 30));

        result.Problem.ShouldNotBeNull();
        result.Problem.Code.ShouldBe(AnnotationProblemCode.NoNameMatch);
    }

    [Fact]
    public void Some_unmatched_names_are_a_notice_with_their_count_and_the_rest_are_used()
    {
        var result = Check(Fasta100, Row("chr1", "gene", 1, 30, "ID=a") + Row("chrX", "gene", 1, 30, "ID=b") + Row("chrY", "gene", 1, 30, "ID=c"));

        result.IsValid.ShouldBeTrue();
        result.GeneCount.ShouldBe(1);
        result.NoticeCodes.ShouldContain(n => n.Code == InputNoticeCode.AnnotationUnmatchedNames && n.Count == 2);
    }

    [Fact]
    public void A_file_with_no_gene_or_CDS_rows_is_refused()
    {
        var result = Check(Fasta100, Row("chr1", "exon", 1, 30) + Row("chr1", "region", 1, 100, "ID=r"));

        result.Problem!.Code.ShouldBe(AnnotationProblemCode.NoFeatures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("##gff-version 3\n")]
    [InlineData("chr1\tsrc\tgene\t1\t30\n")] // five columns
    [InlineData("chr1\tsrc\tgene\tx\t30\t.\t+\t.\tID=a\n")] // start is not a number
    [InlineData("chr1\tsrc\tgene\t0\t30\t.\t+\t.\tID=a\n")] // start below 1
    [InlineData("chr1\tsrc\tgene\t30\t10\t.\t+\t.\tID=a\n")] // start after end
    public void A_file_with_no_valid_row_is_refused(string gff)
        => Check(Fasta100, gff).Problem!.Code.ShouldBe(AnnotationProblemCode.NoFeatures);

    [Fact]
    public void Malformed_rows_are_skipped_with_a_notice_naming_the_count_and_the_first_line()
    {
        var gff = "##gff-version 3\n" + Row("chr1", "gene", 1, 30, "ID=a") + "broken line\n" + Row("chr1", "gene", 0, 5, "ID=b");

        var result = Check(Fasta100, gff);

        result.IsValid.ShouldBeTrue();
        result.GeneCount.ShouldBe(1);
        result.NoticeCodes.ShouldContain(n => n.Code == InputNoticeCode.AnnotationLinesSkipped && n.Count == 2 && n.Other == 3);
    }

    [Fact]
    public void Rows_after_the_embedded_FASTA_marker_are_not_annotation()
    {
        var gff = Row("chr1", "gene", 1, 30, "ID=a") + "##FASTA\n" + Row("chr1", "gene", 40, 60, "ID=b");

        Check(Fasta100, gff).GeneCount.ShouldBe(1);
    }

    [Fact]
    public void A_feature_past_the_end_of_its_sequence_is_dropped_with_a_count()
    {
        var gff = Row("chr1", "gene", 1, 30, "ID=a") + Row("chr1", "gene", 90, 150, "ID=b");

        var result = Check(Fasta100, gff);

        result.IsValid.ShouldBeTrue();
        result.GeneCount.ShouldBe(1);
        result.NoticeCodes.ShouldContain(n => n.Code == InputNoticeCode.AnnotationFeaturesPastEnd && n.Count == 1);
    }

    [Fact]
    public void Every_feature_past_the_end_leaves_no_placed_gene_and_is_refused()
        => Check(Fasta100, Row("chr1", "gene", 90, 150)).Problem!.Code.ShouldBe(AnnotationProblemCode.NoneFit);

    [Fact]
    public void On_a_circular_sequence_a_feature_across_the_origin_is_kept()
    {
        var circular = "chr1\tsrc\tregion\t1\t100\t.\t+\t.\tID=r;Is_circular=true\n" + Row("chr1", "gene", 90, 110, "ID=wrap");

        var result = Check(Fasta100, circular);

        result.GeneCount.ShouldBe(1);
        result.NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.AnnotationFeaturesPastEnd);
    }

    [Fact]
    public void A_linear_run_drops_that_feature_even_when_the_file_declares_the_circle()
    {
        var circular = "chr1\tsrc\tregion\t1\t100\t.\t+\t.\tID=r;Is_circular=true\n" + Row("chr1", "gene", 90, 110, "ID=wrap") + Row("chr1", "gene", 1, 30, "ID=ok");

        Check(Fasta100, circular, Topology.Linear).GeneCount.ShouldBe(1);
    }

    [Fact]
    public void A_circular_run_keeps_the_wrap_even_when_the_file_does_not_say_circular()
        => Check(Fasta100, Row("chr1", "gene", 90, 110, "ID=wrap"), Topology.Circular).GeneCount.ShouldBe(1);

    [Fact]
    public void FASTA_line_wrapping_digits_and_blank_space_do_not_change_the_length()
    {
        var fasta = ">chr1\nACGTACGTAC\nGTACGTACGT\n\n  ACGTACGTAC 30\n";

        Check(fasta, Row("chr1", "gene", 1, 30)).IsValid.ShouldBeTrue();
        Check(fasta, Row("chr1", "gene", 1, 31)).Problem!.Code.ShouldBe(AnnotationProblemCode.NoneFit);
    }

    [Fact]
    public void A_GenBank_file_cannot_be_paired()
    {
        var gb = Write("a.gb", "LOCUS       T1                     12 bp    DNA     linear   UNK 01-JAN-1980\nORIGIN\n        1 acgtacgtac gt\n//\n");

        var result = AnnotationPairValidator.Check(gb, Write("a.gff3", Row("T1", "gene", 1, 5)));

        result.Problem!.Code.ShouldBe(AnnotationProblemCode.NotFasta);
    }

    [Fact]
    public void A_missing_file_is_unreadable_and_an_oversized_one_is_refused_unread()
    {
        var fasta = Write("a.fasta", Fasta100);

        AnnotationPairValidator.Check(fasta, Path.Combine(_dir, "nope.gff3")).Problem!.Code.ShouldBe(AnnotationProblemCode.FileUnreadable);

        var gff = Write("big.gff3", Row("chr1", "gene", 1, 30));
        AnnotationPairValidator.Check(fasta, gff, Topology.Auto, new InputLimits { MaxFileBytes = 10 }).Problem!.Code.ShouldBe(AnnotationProblemCode.FileTooLarge);
    }

    [Fact]
    public void A_multi_record_FASTA_pairs_by_name_per_record()
    {
        var fasta = ">a\n" + Seq(60) + "\n>b\n" + Seq(60) + "\n";
        var gff = Row("a", "gene", 1, 30, "ID=1") + Row("b", "gene", 1, 30, "ID=2") + Row("b", "gene", 31, 60, "ID=3");

        var result = Check(fasta, gff);

        result.GeneCount.ShouldBe(3);
    }
}
