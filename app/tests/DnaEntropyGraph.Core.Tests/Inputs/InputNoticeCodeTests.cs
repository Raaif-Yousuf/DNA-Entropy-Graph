using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Review of #63: a notice has a machine code (and the numbers it mentions) next to its English text, so the
/// UI shows Resources.resw copy keyed by the code and the text stays a log/parity detail (Hard Rule 13).
/// </summary>
public sealed class InputNoticeCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-notice-" + Guid.NewGuid().ToString("N"));

    public InputNoticeCodeTests() => Directory.CreateDirectory(_dir);

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

    private InputValidationResult Check(string name, string content, bool rna = false, AmbiguityPolicy policy = AmbiguityPolicy.Keep, InputFormat format = InputFormat.Auto)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return InputFileValidator.Validate(path, format, policy, rna);
    }

    private static InputNotice Single(InputValidationResult result, InputNoticeCode code)
        => result.NoticeCodes.Where(n => n.Code == code).ShouldHaveSingleItem();

    [Fact]
    public void U_converted_to_T_carries_the_count()
        => Single(Check("rna.fasta", ">r\nACGUACGUACGU\n", rna: true), InputNoticeCode.RnaConverted).Count.ShouldBe(3);

    [Fact]
    public void Ambiguity_codes_that_are_kept_carry_the_count()
        => Single(Check("n.fasta", ">r\nACGTNNACGTAC\n"), InputNoticeCode.AmbiguityKept).Count.ShouldBe(2);

    [Fact]
    public void Ambiguity_codes_that_are_masked_carry_the_count()
        => Single(Check("n.fasta", ">r\nACGTRYACGTAC\n", policy: AmbiguityPolicy.Mask), InputNoticeCode.AmbiguityMasked).Count.ShouldBe(2);

    [Fact]
    public void A_short_sequence_carries_its_length()
        => Single(Check("s.fasta", ">r\nACGT\n"), InputNoticeCode.ShortSequence).Count.ShouldBe(4);

    [Fact]
    public void Digits_removed_carry_the_count()
        => Single(Check("d.fasta", ">r\n1 ACGTACGTAC 11 GTAC\n"), InputNoticeCode.DigitsRemoved).Count.ShouldBe(3);

    [Fact]
    public void A_header_line_in_pasted_text_is_noted_as_ignored()
        => Single(Check("h.txt", ">just a header\nACGTACGTACGT\n", format: InputFormat.Plain), InputNoticeCode.LeadingHeaderIgnored).ShouldNotBeNull();

    [Fact]
    public void A_FASTA_record_with_no_sequence_is_noted_with_its_position()
        => Single(Check("e.fasta", ">a\nACGTACGTAC\n>b\n>c\nACGTACGTAC\n"), InputNoticeCode.RecordSkippedNoSequence).Count.ShouldBe(2);

    [Fact]
    public void Empty_FASTA_headers_carry_the_count()
        => Single(Check("eh.fasta", ">\nACGTACGTAC\n>\nACGTACGTAC\n"), InputNoticeCode.EmptyHeaders).Count.ShouldBe(2);

    [Fact]
    public void Repeated_FASTA_ids_carry_the_count_of_ids()
        => Single(Check("dup.fasta", ">x\nACGTACGTAC\n>x\nACGTACGTAC\n>y\nACGTACGTAC\n>y\nACGTACGTAC\n"), InputNoticeCode.RepeatedIds).Count.ShouldBe(2);

    [Fact]
    public void A_GenBank_record_with_no_sequence_is_noted_with_its_position()
    {
        var gb = "LOCUS       EMPTY1                  0 bp    DNA     linear   UNK 01-JAN-1980\nORIGIN\n//\n"
            + "LOCUS       T2                     12 bp    DNA     linear   UNK 01-JAN-1980\nORIGIN\n        1 acgtacgtac gt\n//\n";

        Single(Check("two.gb", gb), InputNoticeCode.GenBankRecordSkippedNoSequence).Count.ShouldBe(1);
    }

    [Fact]
    public void A_clean_single_record_file_has_no_notice_codes()
        => Check("ok.fasta", ">r\nACGTACGTACGT\n").NoticeCodes.ShouldBeEmpty();

    [Fact]
    public void Every_text_notice_has_a_matching_code()
    {
        // The two lists are built side by side; this catches a site that adds text and forgets the code.
        var result = Check("many.fasta", ">x\n1 ACGTNNACGTAC\n>x\nACGTACGTAC\n>\nACGT\n");

        result.NoticeCodes.Count.ShouldBe(result.Notices.Count);
    }

    [Fact]
    public void The_summary_notices_that_the_pill_already_shows_have_codes_too()
    {
        var result = Check("multi.fasta", ">a\nACGTACGTAC\n>b\nACGTACGTAC\n");

        Single(result, InputNoticeCode.RecordsRead).Count.ShouldBe(2);
    }
}
