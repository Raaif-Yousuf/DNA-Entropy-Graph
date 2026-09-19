using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// <see cref="GenBankLite" /> is a deliberately partial local sniff (LOCUS/ORIGIN/gene-CDS
/// count only, no full feature-table parse - see its class docs), so these tests check it
/// against the repo's own real GenBank fixture (<c>worker/tests/data/sample.gb</c>, also
/// used by the Python worker's own tests and by <see cref="SequenceValidatorParityTests" />)
/// rather than a golden vector: there is no shared contract fixture for reader-level
/// GenBank/FASTA parity today (flagged in this lane's report).
/// </summary>
public sealed class GenBankLiteTests
{
    private static string SampleGbPath => Path.Combine(ContractFixtures.RepoRoot, "worker", "tests", "data", "sample.gb");

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
    public void Multiple_records_separated_by_a_terminator_line_are_all_returned()
    {
        var text =
            "LOCUS       rec_one   4 bp\n"
            + "FEATURES\n"
            + "     gene            1..4\n"
            + "ORIGIN\n"
            + "        1 acgt\n"
            + "//\n"
            + "LOCUS       rec_two   4 bp\n"
            + "ORIGIN\n"
            + "        1 ttgg\n"
            + "//\n";

        var result = GenBankLite.Read(text);

        result.Records.Count.ShouldBe(2);
        result.Records[0].LocusName.ShouldBe("rec_one");
        result.Records[0].Seq.ShouldBe("ACGT");
        result.Records[0].GeneCount.ShouldBe(1);
        result.Records[1].LocusName.ShouldBe("rec_two");
        result.Records[1].Seq.ShouldBe("TTGG");
        result.Notices.ShouldContain(n => n.StartsWith("Read 2 record(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_with_an_empty_ORIGIN_block_is_skipped_with_a_notice()
    {
        var text = "LOCUS       empty_one   0 bp\nORIGIN\n//\nLOCUS       real_one   4 bp\nORIGIN\n        1 acgt\n//\n";

        var result = GenBankLite.Read(text);

        result.Records.Count.ShouldBe(1);
        result.Records[0].LocusName.ShouldBe("real_one");
        result.Notices.ShouldContain(n => n.StartsWith("Skipped GenBank record 1", StringComparison.Ordinal));
    }

    [Fact]
    public void No_LOCUS_line_at_all_is_rejected()
    {
        Should.Throw<GenBankReadException>(() => GenBankLite.Read("just some text\nACGT\n"));
    }

    [Fact]
    public void Ambiguity_codes_in_ORIGIN_pass_through_uppercased_untouched()
    {
        var text = "LOCUS       rec   6 bp\nORIGIN\n        1 acgtnn\n//\n";
        var result = GenBankLite.Read(text);
        result.Records[0].Seq.ShouldBe("ACGTNN");
    }
}
