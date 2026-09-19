using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Port of <c>worker/src/dna_entropy/readers/fasta.py</c>'s rules, including issue #350/#351's
/// measured bug: two records whose header sanitizes/keys to the same id must never silently
/// let one overwrite the other - they are kept, numbered separately, and flagged.
/// </summary>
public sealed class FastaLiteTests
{
    [Fact]
    public void A_single_record_reads_its_header_and_concatenated_sequence_lines()
    {
        var result = FastaLite.Read(">seq1 my plasmid\nACGT\nACGT\n");

        result.Records.Count.ShouldBe(1);
        result.Records[0].Header.ShouldBe("seq1 my plasmid");
        result.Records[0].Seq.ShouldBe("ACGTACGT");
    }

    [Fact]
    public void A_multi_record_fasta_returns_every_record_not_just_the_first()
    {
        var result = FastaLite.Read(">seq1\nACGT\n>seq2\nTTTT\n>seq3\nGGGG\n");

        result.Records.Count.ShouldBe(3);
        result.Records.Select(r => r.Seq).ShouldBe(["ACGT", "TTTT", "GGGG"]);
        result.Notices.ShouldContain("Read 3 record(s) from the FASTA (all processed).");
    }

    [Fact]
    public void No_header_line_at_all_is_rejected()
    {
        var ex = Should.Throw<FastaReadException>(() => FastaLite.Read("ACGTACGT"));
        ex.Message.ShouldBe("No FASTA records found (expected a '>' header line).");
    }

    [Fact]
    public void A_header_with_no_sequence_lines_is_skipped_with_a_notice_never_the_header_text()
    {
        var result = FastaLite.Read(">seq1 secret name\nACGT\n>seq2 empty one\n>seq3\nTTTT\n");

        result.Records.Count.ShouldBe(2);
        result.Notices.ShouldContain(n => n.StartsWith("Skipped FASTA record 2", StringComparison.Ordinal));
        result.Notices.ShouldAllBe(n => !n.Contains("secret", StringComparison.Ordinal) && !n.Contains("empty one", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_record_empty_is_a_hard_error()
    {
        var ex = Should.Throw<FastaReadException>(() => FastaLite.Read(">seq1\n>seq2\n"));
        ex.Message.ShouldBe("The FASTA file has no records with a sequence.");
    }

    [Fact]
    public void A_bare_header_with_no_name_is_tolerated_and_flagged()
    {
        var result = FastaLite.Read(">\nACGT\n>named\nTTTT\n");

        result.Records.Count.ShouldBe(2);
        result.Notices.ShouldContain("1 record(s) have an empty header line (a bare '>' with no name).");
    }

    [Fact]
    public void Two_records_whose_id_collides_are_both_kept_and_flagged_not_silently_overwritten()
    {
        // issue #351: id is the header up to its first whitespace, not the full header line.
        var result = FastaLite.Read(">seq1 first description\nACGT\n>seq1 second description\nTTTT\n");

        result.Records.Count.ShouldBe(2); // never silently dropped/overwritten
        result.Records[0].Seq.ShouldBe("ACGT");
        result.Records[1].Seq.ShouldBe("TTTT");
        result.Notices.ShouldContain(n => n.StartsWith("1 id(s) repeat across records", StringComparison.Ordinal));
        result.Notices.ShouldAllBe(n => !n.Contains("seq1 first description", StringComparison.Ordinal));
    }

    [Fact]
    public void Records_with_the_same_full_header_but_different_ids_do_not_collide()
    {
        var result = FastaLite.Read(">seq1\nACGT\n>seq2\nTTTT\n");
        result.Notices.ShouldNotContain(n => n.Contains("repeat across records", StringComparison.Ordinal));
    }
}
