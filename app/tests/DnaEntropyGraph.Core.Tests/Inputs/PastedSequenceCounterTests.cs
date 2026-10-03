using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>Issue #63: the live "N bases" counter under the Paste sequence box.</summary>
public sealed class PastedSequenceCounterTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("   \r\n ", 0)]
    [InlineData("ACGT", 4)]
    [InlineData("ac gt\nAC\r\nGT", 8)]
    [InlineData(">rec1 a header\nACGT\nAC", 6)]
    [InlineData("  1 acgtacgtac 11 gtac", 14)]
    [InlineData("ACGUN", 5)]
    public void Counts_the_bases_the_validator_will_see(string text, int expected)
        => PastedSequenceCounter.Count(text).ShouldBe(expected);

    [Fact]
    public void A_null_text_counts_zero()
        => PastedSequenceCounter.Count(null).ShouldBe(0);

    [Fact]
    public void Only_the_header_lines_are_skipped_not_a_greater_than_sign_inside_a_sequence()
        => PastedSequenceCounter.Count(">h1\nAC\n>h2\nGT").ShouldBe(4);
}
