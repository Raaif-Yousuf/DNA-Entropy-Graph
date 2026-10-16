using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;
using static DnaEntropyGraph.Core.Tests.Inputs.ReadersParityFixture;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #796 (Hard Rule 2): <see cref="EmblLite" /> reads an EMBL feature table the way the worker's <c>read_embl</c> does, so an
/// unreadable feature location is dropped with the worker's notice, and a location Biopython refuses refuses the file here too.
/// The cases are replayed from <c>tests/contract-fixtures/readers_parity.json</c> (<c>embl_cases</c>), which the worker's
/// <c>test_readers_parity.py</c> replays against <c>read_embl</c>.
/// </summary>
public sealed class EmblLiteParityTests
{
    public static IEnumerable<object[]> Cases() => EmblCases();

    private const string Tail = "Check the file was not truncated or hand-edited, or re-export it from the tool that made it.";

    [Fact]
    public void The_fixture_carries_the_dropped_and_the_refused_shapes_so_the_theory_cannot_go_vacuous()
    {
        var cases = Cases().Select(c => (ParityCase)c[0]).ToList();

        cases.ShouldContain(c => c.Python.Notices.Any(n => n.StartsWith("EMBL record 1: 1 gene feature(s) were dropped", StringComparison.Ordinal)));
        cases.Count(c => c.Python.Error is not null).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_workers_read_embl_sequences_gene_counts_notices_and_refusals(ParityCase testCase)
    {
        if (testCase.Python.Error is not null)
        {
            var ex = Should.Throw<GenBankReadException>(() => EmblLite.Read(testCase.Text), $"case {testCase.Id}");

            // The reason is Biopython's own text (an assert has none); the frame and the action are the worker's.
            ex.Message.ShouldStartWith("Could not parse the EMBL file (record 1): ", customMessage: testCase.Id);
            ex.Message.ShouldEndWith(Tail, customMessage: testCase.Id);
            return;
        }

        var result = EmblLite.Read(testCase.Text);

        result.Records.Select(r => (r.Seq, Genes: r.GeneCount > 0 ? r.GeneCount : r.CdsCount))
            .ShouldBe(testCase.Python.Records.Select(r => (r.Seq, r.GeneCount)), $"case {testCase.Id} records");
        result.Notices.ShouldBe(testCase.Python.Notices, $"case {testCase.Id} notices");
    }

    [Fact]
    public void A_dropped_location_raises_the_same_notice_code_the_genbank_path_does()
    {
        var droppedCase = Cases().Select(c => (ParityCase)c[0]).First(c => c.Id == "embl_unreadable_gene_location_is_dropped_with_a_notice");

        var result = EmblLite.Read(droppedCase.Text);

        result.NoticeCodes.ShouldContain(new InputNotice(InputNoticeCode.GenBankFeaturesDropped, 1, 1));
    }

    [Fact]
    public void An_origin_wrap_is_read_on_a_circular_record_and_dropped_on_a_linear_one_by_the_ID_line()
    {
        // MEASURED 2026-10-16 with read_embl: the same 6..2 location is a wrapping gene when the ID line says circular (new and old
        // layout) and a dropped feature when it says linear.
        string Embl(string id) => id + "\nXX\nFH   Key             Location/Qualifiers\nFH\nFT   gene            6..2\nFT                   /gene=\"x\"\nXX\n"
            + "SQ   Sequence 8 BP; 2 A; 2 C; 2 G; 2 T; 0 other;\n     acgtacgt        8\n//\n";

        EmblLite.Read(Embl("ID   toy; SV 1; circular; genomic DNA; STD; UNC; 8 BP.")).Records.Single().GeneCount.ShouldBe(1);
        EmblLite.Read(Embl("ID   toy standard; circular DNA; UNC; 8 BP.")).Records.Single().GeneCount.ShouldBe(1);
        var linear = EmblLite.Read(Embl("ID   toy; SV 1; linear; genomic DNA; STD; UNC; 8 BP."));
        linear.Records.Single().GeneCount.ShouldBe(0);
        linear.NoticeCodes.ShouldContain(n => n.Code == InputNoticeCode.GenBankFeaturesDropped);
    }

    [Fact]
    public void A_refused_location_never_echoes_the_location_text()
    {
        var refused = Cases().Select(c => (ParityCase)c[0]).First(c => c.Id == "embl_empty_join_location_refuses_the_file");

        var ex = Should.Throw<GenBankReadException>(() => EmblLite.Read(refused.Text));

        ex.Message.ShouldNotContain("join()");
    }

    [Fact]
    public void A_feature_table_with_no_end_marker_before_the_sequence_is_still_read()
    {
        // Measured: SQ ends the table when there is no XX line.
        var text = "ID   toy; SV 1; linear; genomic DNA; STD; UNC; 8 BP.\nXX\nFH   Key             Location/Qualifiers\nFH\nFT   gene            1..4\nFT                   /gene=\"x\"\n"
            + "SQ   Sequence 8 BP; 2 A; 2 C; 2 G; 2 T; 0 other;\n     acgtacgt        8\n//\n";

        EmblLite.Read(text).Records.Single().GeneCount.ShouldBe(1);
    }

    [Fact]
    public void A_qualifier_line_missing_its_slash_after_a_valueless_qualifier_refuses_the_file_like_the_genbank_table()
    {
        // Biopython's parse_feature is shared by both scanners: a continuation line after /pseudo (no value) raises.
        var text = "ID   toy; SV 1; linear; genomic DNA; STD; UNC; 8 BP.\nXX\nFH   Key             Location/Qualifiers\nFH\nFT   gene            1..4\nFT                   /pseudo\nFT                   zzz\nXX\n"
            + "SQ   Sequence 8 BP; 2 A; 2 C; 2 G; 2 T; 0 other;\n     acgtacgt        8\n//\n";

        Should.Throw<GenBankReadException>(() => EmblLite.Read(text)).Message.ShouldStartWith("Could not parse the EMBL file (record 1): ");
    }
}
