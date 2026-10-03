using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Tests.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

/// <summary>
/// Issue #102: the per-contig numbers on the Results page are read from the worker's own summary file
/// (<c>stats.txt</c> / <c>&lt;name&gt;.summary.txt</c>), never recomputed. The fixture is a real mock-pipeline
/// summary; <c>worker/tests/test_run_summary_fixture.py</c> fails if the worker's output drifts from it.
/// </summary>
public sealed class RunSummaryReaderTests
{
    private static string Fixture() => File.ReadAllText(ContractFixtures.Path("run_summary_two_contigs.txt"));

    [Fact]
    public void Parse_reads_the_overall_block_of_the_worker_summary()
    {
        var summary = RunSummaryReader.Parse(Fixture());

        summary.Name.ShouldBe("two");
        summary.Records.ShouldBe(2);
        summary.TotalLength.ShouldBe(64);
        summary.MeanAll.ShouldBe(1.6341, 1e-9);
        summary.MinAll.ShouldBe(0.8985, 1e-9);
        summary.MaxAll.ShouldBe(1.9810, 1e-9);
    }

    [Fact]
    public void Parse_reads_one_entry_per_contig_with_its_own_numbers_in_file_order()
    {
        var summary = RunSummaryReader.Parse(Fixture());

        summary.Contigs.Select(c => c.Name).ShouldBe(["two_1", "two_2"]);
        var second = summary.Contigs[1];
        second.Length.ShouldBe(32);
        second.Mean.ShouldBe(1.5784, 1e-9);
        second.Min.ShouldBe(1.0044, 1e-9);
        second.Max.ShouldBe(1.8709, 1e-9);
        second.Direction.ShouldBe("both-combined");
        second.Seam.ShouldBe("n/a");
    }

    [Fact]
    public void Parse_tolerates_crlf_line_endings()
    {
        var summary = RunSummaryReader.Parse(Fixture().Replace("\n", "\r\n", StringComparison.Ordinal));

        summary.Contigs.Count.ShouldBe(2);
        summary.Contigs[0].Direction.ShouldBe("both-combined");
    }

    [Fact]
    public void Parse_rejects_a_contig_with_no_mean()
    {
        var broken = Fixture().Replace("  entropy mean:     1.5784 bits", string.Empty, StringComparison.Ordinal);

        Should.Throw<InvalidDataException>(() => RunSummaryReader.Parse(broken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a summary at all")]
    [InlineData("DNA-Entropy summary\nname: x\n")]
    public void Parse_rejects_text_that_is_not_a_worker_summary(string text)
    {
        Should.Throw<InvalidDataException>(() => RunSummaryReader.Parse(text));
    }

    [Fact]
    public void Parse_rejects_a_number_that_is_not_a_number()
    {
        var broken = Fixture().Replace("1.6341", "abc", StringComparison.Ordinal);

        Should.Throw<InvalidDataException>(() => RunSummaryReader.Parse(broken));
    }

    [Fact]
    public void The_fixture_is_not_vacuous()
    {
        Fixture().ShouldContain("[two_1]");
        Fixture().ShouldContain("[two_2]");
    }
}
