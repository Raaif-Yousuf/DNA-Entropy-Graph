using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #611 (Hard Rule 2): the record-header and feature-wrapping shapes the worker's Biopython reader refuses and
/// <see cref="GenBankLite" /> used to accept. Every verdict and refusal text below was MEASURED 2026-10-16 by running
/// <c>dna_entropy.readers.genbank.read_genbank</c> on the same text in <c>worker\.venv</c>.
/// </summary>
public sealed class GenBankHeaderParityTests
{
    private const string Locus = "LOCUS       a   4 bp\n";
    private const string Origin = "ORIGIN\n        1 acgt\n//\n";
    private const string Tail = " Check the file was not truncated or hand-edited, and that its ORIGIN block matches its own LOCUS/FEATURES.";

    [Fact]
    public void A_record_terminator_in_a_records_header_is_refused_with_the_workers_text()
    {
        // Biopython parse_header: "//" before any feature table or sequence header.
        var text = Locus + "DEFINITION x\n//\nLOCUS       b   4 bp\n" + Origin;

        var ex = Should.Throw<GenBankReadException>(() => GenBankLite.Read(text));

        ex.Message.ShouldBe("Could not parse the GenBank file (record 1): Premature end of sequence data marker '//' found." + Tail);
    }

    [Fact]
    public void The_refusal_names_the_record_by_its_position_counting_the_earlier_ones()
    {
        var text = Locus + Origin + "LOCUS       b   4 bp\n//  \n";

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(text)).Message.ShouldStartWith("Could not parse the GenBank file (record 2): Premature end of sequence data marker");
    }

    [Fact]
    public void A_line_that_only_starts_with_slashes_is_a_header_line_not_a_terminator()
    {
        // Biopython compares the right-stripped line with "//" exactly, so "//x" is read as header text.
        GenBankLite.Read(Locus + "//x\n" + Origin).Records.Count.ShouldBe(1);
    }

    [Fact]
    public void A_header_that_ends_the_file_is_refused()
    {
        var ex = Should.Throw<GenBankReadException>(() => GenBankLite.Read(Locus + "DEFINITION  x\n"));

        ex.Message.ShouldBe("Could not parse the GenBank file (record 1): Premature end of line during sequence data." + Tail);
    }

    [Fact]
    public void A_later_records_header_that_ends_the_file_is_refused_even_after_a_good_record()
    {
        var text = Locus + Origin + "LOCUS       b   4 bp\nDEFINITION  x\n";

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(text)).Message.ShouldStartWith("Could not parse the GenBank file (record 2): Premature end of line during sequence data.");
    }

    [Fact]
    public void A_sequence_header_that_ends_the_file_before_any_sequence_is_refused()
    {
        var ex = Should.Throw<GenBankReadException>(() => GenBankLite.Read(Locus + "BASE COUNT  1 a\n"));

        ex.Message.ShouldBe("Could not parse the GenBank file (record 1): Premature end of file." + Tail);
    }

    [Fact]
    public void A_sequence_header_then_a_terminator_is_still_a_skipped_empty_record()
    {
        // Measured: the worker reads it as a record with no sequence and refuses the file only because nothing is left.
        var text = Locus + "BASE COUNT  1 a\n//\nLOCUS       b   4 bp\n" + Origin;

        var result = GenBankLite.Read(text);

        result.Records.Select(r => r.Seq).ShouldBe(["ACGT"]);
    }

    [Fact]
    public void A_lone_closing_parenthesis_after_the_location_is_joined_onto_it_and_the_file_is_refused()
    {
        // Biopython parse_feature: a first qualifier line that starts with ")" is appended to the location ("1..4)"), whose parse then
        // trips `assert value == text`.
        var text = Locus + "FEATURES             Location/Qualifiers\n     gene            1..4\n                     )\n                     /gene=\"x\"\n" + Origin;

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(text)).Message.ShouldStartWith("Could not parse the GenBank file (record 1): ");
    }

    [Theory]
    [InlineData("     CDS             1..4\n                     )\n                     /gene=\"x\"\n")]
    [InlineData("     source          1..4\n                     )\n                     /organism=\"x\"\n")]
    public void The_stray_parenthesis_is_refused_on_any_feature_key_not_only_genes(string feature)
    {
        var text = Locus + "FEATURES             Location/Qualifiers\n" + feature + Origin;

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(text));
    }

    [Fact]
    public void A_closing_parenthesis_that_completes_a_wrapped_join_is_still_read()
    {
        var text = Locus + "FEATURES             Location/Qualifiers\n     gene            join(1..2,\n                     3..4\n                     )\n                     /gene=\"x\"\n" + Origin;

        GenBankLite.Read(text).Records.Single().GeneCount.ShouldBe(1);
    }

    [Fact]
    public void A_closing_parenthesis_line_after_a_qualifier_is_a_continuation_and_is_read()
    {
        // Measured: the worker reads this record (the ")" extends the previous qualifier value).
        var text = Locus + "FEATURES             Location/Qualifiers\n     gene            1..4\n                     /gene=\"x\"\n                     )\n" + Origin;

        GenBankLite.Read(text).Records.Single().GeneCount.ShouldBe(1);
    }
}
