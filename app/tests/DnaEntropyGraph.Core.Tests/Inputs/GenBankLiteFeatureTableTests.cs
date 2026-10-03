using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// #548: the feature-table rules of Bio.GenBank.Scanner (parse_features / parse_feature) that make the
/// worker refuse a file, ported so the app refuses it locally (Hard Rule 2). Only refusals are ported;
/// every shape the worker accepts must still read.
/// </summary>
public sealed class GenBankLiteFeatureTableTests
{
    private const string Header = "LOCUS       rec   8 bp    DNA     linear   UNK 01-JAN-1980\nFEATURES             Location/Qualifiers\n";
    private const string Footer = "ORIGIN\n        1 acgtacgt\n//\n";
    private static readonly string Indent = new(' ', 21);

    private static string Gb(params string[] featureLines) => Header + string.Concat(featureLines.Select(l => l + "\n")) + Footer;

    [Fact]
    public void A_well_formed_feature_table_still_reads()
    {
        var text = Gb("     gene            1..4", Indent + "/gene=\"geneA\"", "     CDS             1..4", Indent + "/note=\"two", Indent + "lines\"", Indent + "/pseudo");

        var result = GenBankLite.Read(text);

        result.Records.Count.ShouldBe(1);
        result.Records[0].GeneCount.ShouldBe(1);
        result.Records[0].CdsCount.ShouldBe(1);
    }

    [Fact]
    public void A_qualifier_line_missing_its_slash_is_refused()
    {
        var text = Gb("     gene            1..4", Indent + "/gene=\"geneA\"", "     gene            5..8", Indent + "gene=\"geneB\"");

        var ex = Should.Throw<GenBankReadException>(() => GenBankLite.Read(text));

        ex.Message.ShouldContain("slash");
        ex.Message.ShouldNotContain("\u2014");
    }

    [Fact]
    public void A_feature_line_exactly_at_the_qualifier_column_is_refused()
    {
        // Biopython right-strips the line, then indexes line[21]: a feature line that ends exactly at column 21 raises IndexError.
        var text = Gb("     CDS             1..4", "     CDS        1..42", "     gene            5..8");

        var ex = Should.Throw<GenBankReadException>(() => GenBankLite.Read(text));

        ex.Message.ShouldContain("shorter");
        ex.Message.ShouldNotContain("\u2014");
    }

    [Fact]
    public void A_feature_line_shorter_than_the_qualifier_column_is_skipped_as_the_worker_does()
    {
        // len < 21 after the right-strip is only a warning in Biopython: the line is dropped, the file reads.
        var text = Gb("     gene            1..4", "     CDS", "     gene            5..8");

        GenBankLite.Read(text).Records.Count.ShouldBe(1);
    }

    [Fact]
    public void A_blank_line_between_qualifiers_is_allowed()
    {
        var text = Gb("     gene            1..4", Indent + "/gene=\"geneA\"", string.Empty, Indent + "/locus_tag=\"X\"");

        GenBankLite.Read(text).Records.Count.ShouldBe(1);
    }

    [Fact]
    public void A_continuation_after_a_valueless_qualifier_is_refused()
    {
        var text = Gb("     gene            1..4", Indent + "/pseudo", Indent + "stray text");

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(text));
    }

    [Fact]
    public void An_unterminated_quoted_qualifier_is_refused()
    {
        var text = Gb("     gene            1..4", Indent + "/note=\"never closed");

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(text));
    }

    [Fact]
    public void A_features_line_in_a_later_record_is_checked_too()
    {
        var good = Gb("     gene            1..4", Indent + "/gene=\"a\"");
        var bad = Gb("     gene            1..4", Indent + "gene=\"b\"");

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(good + bad));
    }

    [Fact]
    public void Qualifier_like_text_inside_the_origin_block_is_not_a_feature_table()
    {
        GenBankLite.Read(Gb("     gene            1..4", Indent + "/gene=\"a\"")).Records.Count.ShouldBe(1);
    }
}
