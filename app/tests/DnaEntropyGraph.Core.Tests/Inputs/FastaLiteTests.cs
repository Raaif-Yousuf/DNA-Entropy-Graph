using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;
using static DnaEntropyGraph.Core.Tests.Inputs.ReadersParityFixture;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Port of <c>worker/src/dna_entropy/readers/fasta.py</c>'s rules. The records, notices and error
/// text are replayed from <c>tests/contract-fixtures/readers_parity.json</c> (issue #393), which the
/// worker's own <c>test_readers_parity.py</c> also replays against <c>read_fasta</c>, so a wording
/// change in either reader fails a test until the fixture and both ports agree. The rest are the
/// negative and privacy assertions the fixture cannot express.
/// </summary>
public sealed class FastaLiteTests
{
    public static IEnumerable<object[]> Cases() => FastaCases();

    [Fact]
    public void The_fixture_carries_the_cases_the_issue_names_so_the_theory_cannot_go_vacuous()
    {
        var ids = Cases().Select(c => ((ParityCase)c[0]).Id).ToList();

        ids.ShouldContain("multi_record_three");
        ids.ShouldContain("duplicate_ids_different_descriptions");
        ids.ShouldContain("empty_after_header_is_skipped");
        ids.ShouldContain("every_record_empty");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_workers_read_fasta_records_notices_and_error_text(ParityCase testCase)
    {
        if (testCase.Python.Error is { } expectedError)
        {
            var ex = Should.Throw<FastaReadException>(() => FastaLite.Read(testCase.Text), $"case {testCase.Id}");
            ex.Message.ShouldBe(expectedError, $"case {testCase.Id} diverges from the worker's error text");
            return;
        }

        var result = FastaLite.Read(testCase.Text);

        result.Records.Select(r => (r.Header, r.Seq))
            .ShouldBe(testCase.Python.Records.Select(r => (r.Header ?? "", r.Seq)), $"case {testCase.Id} records");
        result.Notices.ShouldBe(testCase.Python.Notices, $"case {testCase.Id} diverges from the worker's notices");
    }

    [Fact]
    public void A_single_record_reads_its_header_and_concatenated_sequence_lines()
    {
        var result = FastaLite.Read(">seq1 my plasmid\nACGT\nACGT\n");

        result.Records.Count.ShouldBe(1);
        result.Records[0].Header.ShouldBe("seq1 my plasmid");
        result.Records[0].Seq.ShouldBe("ACGTACGT");
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
