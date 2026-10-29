using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #847: a gene whose positions do not fit the sequence is left out, and a gene that is left out is replaced by the CDS with the same gene id, as the
/// worker's <c>_features_of</c> does (#331, #694). The notice TEXT is pinned by the parity fixture (<c>readers_parity.json</c>); these tests pin the machine
/// codes the New run page turns into copy, and how a feature's id is chosen.
/// </summary>
public sealed class GenBankFeatureFallbackTests
{
    private const string Head = "LOCUS       a                  8 bp    DNA     linear   UNK 01-JAN-2000\nFEATURES             Location/Qualifiers\n";
    private const string Tail = "ORIGIN\n        1 acgtacgt\n//\n";

    private static string Feature(string key, string location, params string[] qualifiers)
        => $"     {key,-16}{location}\n" + string.Concat(qualifiers.Select(q => $"                     {q}\n"));

    private static GenBankLiteResult Read(params string[] features) => GenBankLite.Read(Head + string.Concat(features) + Tail);

    [Fact]
    public void A_gene_outside_the_sequence_is_counted_out_with_one_out_of_range_code()
    {
        var result = Read(Feature("gene", "20..30", "/gene=\"x\""));

        result.Records.Single().Mapped.ShouldBe(0);
        result.NoticeCodes.ShouldContain(new InputNotice(InputNoticeCode.GenBankFeaturesOutOfRange, 1, 1));
        result.NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankGeneStoodInByCds);
    }

    [Fact]
    public void A_cds_of_the_same_gene_stands_in_and_both_codes_say_so()
    {
        var result = Read(Feature("gene", "20..30", "/gene=\"x\""), Feature("CDS", "1..6", "/gene=\"x\""));

        result.Records.Single().Mapped.ShouldBe(1);
        result.NoticeCodes.ShouldContain(new InputNotice(InputNoticeCode.GenBankFeaturesOutOfRange, 1, 1));
        result.NoticeCodes.ShouldContain(new InputNotice(InputNoticeCode.GenBankGeneStoodInByCds, 1, 1));
    }

    [Fact]
    public void A_feature_id_is_the_gene_qualifier_before_the_locus_tag_and_a_quoted_value_loses_its_quotes()
    {
        // The CDS names the gene "b", so it only stands in if the gene's id is /gene (not the earlier /locus_tag).
        var result = Read(Feature("gene", "20..30", "/locus_tag=\"a\"", "/gene=\"b\""), Feature("CDS", "1..6", "/gene=b"));

        result.Records.Single().Mapped.ShouldBe(1);
    }

    [Fact]
    public void A_gene_with_only_a_locus_tag_is_matched_to_the_cds_by_the_locus_tag()
    {
        var result = Read(Feature("gene", "20..30", "/locus_tag=\"lt1\""), Feature("CDS", "1..6", "/locus_tag=\"lt1\""));

        result.Records.Single().Mapped.ShouldBe(1);
    }

    [Fact]
    public void A_gene_with_no_name_is_not_replaced_by_a_cds()
    {
        var result = Read(Feature("gene", "20..30"), Feature("CDS", "1..6"));

        result.Records.Single().Mapped.ShouldBe(0);
        result.NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankGeneStoodInByCds);
    }

    [Fact]
    public void A_gene_kept_with_the_same_id_means_the_cds_does_not_stand_in()
    {
        var result = Read(Feature("gene", "20..30", "/gene=\"x\""), Feature("gene", "1..4", "/gene=\"x\""), Feature("CDS", "1..3", "/gene=\"x\""));

        result.Records.Single().Mapped.ShouldBe(1);
        result.NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankGeneStoodInByCds);
    }

    [Fact]
    public void A_single_location_ending_in_a_greater_than_mark_may_run_past_the_sequence_but_a_join_may_not()
    {
        Read(Feature("gene", "1..>40", "/gene=\"x\"")).Records.Single().Mapped.ShouldBe(1);
        Read(Feature("gene", "join(1..2,4..>40)", "/gene=\"x\"")).Records.Single().Mapped.ShouldBe(0);
    }

    [Fact]
    public void A_gene_across_the_origin_of_a_circular_record_fits()
    {
        var text = "LOCUS       a                  8 bp    DNA     circular   UNK 01-JAN-2000\nFEATURES             Location/Qualifiers\n"
            + Feature("gene", "6..2", "/gene=\"x\"") + Tail;

        var result = GenBankLite.Read(text);

        result.Records.Single().Mapped.ShouldBe(1);
        result.NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankFeaturesOutOfRange);
    }

    [Fact]
    public void An_EMBL_record_replaces_a_gene_outside_the_sequence_with_its_cds_too()
    {
        var text = "ID   toy; SV 1; linear; genomic DNA; STD; UNC; 8 BP.\nXX\nFH   Key             Location/Qualifiers\nFH\n"
            + "FT   gene            20..30\nFT                   /gene=\"x\"\nFT   CDS             1..6\nFT                   /gene=\"x\"\nXX\n"
            + "SQ   Sequence 8 BP; 2 A; 2 C; 2 G; 2 T; 0 other;\n     acgtacgt        8\n//\n";

        var result = EmblLite.Read(text);

        result.Records.Single().Mapped.ShouldBe(1);
        result.NoticeCodes.ShouldContain(new InputNotice(InputNoticeCode.GenBankFeaturesOutOfRange, 1, 1));
        result.NoticeCodes.ShouldContain(new InputNotice(InputNoticeCode.GenBankGeneStoodInByCds, 1, 1));
    }
}
