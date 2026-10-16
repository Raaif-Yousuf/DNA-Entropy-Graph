using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;
using static DnaEntropyGraph.Core.Tests.Inputs.ReadersParityFixture;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// <see cref="GenBankLite" /> is a deliberately partial local sniff (LOCUS/ORIGIN/gene-CDS
/// count only, no full feature-table parse - see its class docs). Its records' sequences, gene
/// counts, notices and error text are replayed from
/// <c>tests/contract-fixtures/readers_parity.json</c> (issue #393), which the worker's own
/// <c>test_readers_parity.py</c> also replays against <c>read_genbank</c>. Where the port's text
/// deliberately differs from Biopython's, the fixture case carries a <c>csharp_divergence</c> block
/// with the reason, so the difference is recorded in one shared place instead of hidden here.
/// </summary>
public sealed class GenBankLiteTests
{
    private static string SampleGbPath => Path.Combine(ContractFixtures.RepoRoot, "worker", "tests", "data", "sample.gb");

    public static IEnumerable<object[]> Cases() => GenBankCases();

    [Fact]
    public void The_fixture_carries_the_cases_the_issue_names_so_the_theory_cannot_go_vacuous()
    {
        var ids = Cases().Select(c => ((ParityCase)c[0]).Id).ToList();

        ids.ShouldContain("multi_record_two");
        ids.ShouldContain("no_locus_line");
        ids.ShouldContain("truncated_mid_feature_table");
        ids.ShouldContain("empty_origin_record_skipped_second_kept");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_workers_read_genbank_sequences_gene_counts_notices_and_error_text(ParityCase testCase)
    {
        var expectedError = testCase.CSharpDivergence is { Error: not null } d ? d.Error : testCase.Python.Error;
        if (expectedError is not null)
        {
            var ex = Should.Throw<GenBankReadException>(() => GenBankLite.Read(testCase.Text), $"case {testCase.Id}");
            ex.Message.ShouldBe(expectedError, $"case {testCase.Id} error text");
            return;
        }

        var result = GenBankLite.Read(testCase.Text);

        result.Records.Select(r => (r.Seq, Genes: r.GeneCount > 0 ? r.GeneCount : r.CdsCount))
            .ShouldBe(testCase.Python.Records.Select(r => (r.Seq, r.GeneCount)), $"case {testCase.Id} records");
        var expectedNotices = testCase.CSharpDivergence?.Notices ?? testCase.Python.Notices;
        result.Notices.ShouldBe(expectedNotices, $"case {testCase.Id} notices");
    }

    [Fact]
    public void Divergence_blocks_always_say_why()
    {
        foreach (var c in Cases().Select(c => (ParityCase)c[0]).Where(c => c.CSharpDivergence is not null))
        {
            c.CSharpDivergence!.Reason.ShouldNotBeNullOrWhiteSpace($"case {c.Id} diverges without a reason");
        }
    }

    [Fact]
    public void Reads_the_locus_name_sequence_and_gene_CDS_counts_from_the_real_sample_fixture()
    {
        var result = GenBankLite.ReadFile(SampleGbPath);

        result.Records.Count.ShouldBe(1);
        var record = result.Records[0];
        record.LocusName.ShouldBe("toy_locus");
        record.Seq.Length.ShouldBe(126); // matches the LOCUS line's own "126 bp"
        record.GeneCount.ShouldBe(2); // geneA, geneB
        record.CdsCount.ShouldBe(1);
    }

    [Fact]
    public void Ambiguity_codes_in_ORIGIN_pass_through_uppercased_untouched()
    {
        var text = "LOCUS       rec   6 bp\nORIGIN\n        1 acgtnn\n//\n";
        var result = GenBankLite.Read(text);
        result.Records[0].Seq.ShouldBe("ACGTNN");
    }
}
