using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cloud;

/// <summary>Issue #929: the count of renamed-aside files is a first-class mark on the run record, read back by the Runs row and the run page.</summary>
public sealed class KeptAsideDetailTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(250)]
    public void A_count_survives_a_round_trip_through_a_detail(int count)
        => KeptAsideDetail.TryRead(KeptAsideDetail.Append("The worker reported status 'failed'.", count)).ShouldBe(count);

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void No_mark_is_written_for_a_count_that_is_not_positive(int count)
        => KeptAsideDetail.Append("detail", count).ShouldBe("detail");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no mark here")]
    [InlineData("[kept-aside=]")]
    [InlineData("[kept-aside=abc]")]
    [InlineData("[kept-aside=0]")]
    [InlineData("[kept-aside=99999999999]")]
    public void A_missing_or_malformed_mark_reads_as_zero(string? detail)
        => KeptAsideDetail.TryRead(detail).ShouldBe(0);

    [Fact]
    public void The_last_mark_wins_so_text_that_quotes_a_mark_cannot_override_the_apps_own()
        => KeptAsideDetail.TryRead("[kept-aside=9] quoted " + KeptAsideDetail.Append(null, 2)).ShouldBe(2);

    [Theory]
    [InlineData("detail [kept-aside=1]", 3, "detail [kept-aside=3]")]
    [InlineData("detail", 2, "detail [kept-aside=2]")]
    [InlineData(null, 2, "[kept-aside=2]")]
    [InlineData("detail [kept-aside=4] and more", 0, "detail  and more")]
    public void Set_replaces_the_mark_with_exactly_one(string? detail, int count, string expected)
        => KeptAsideDetail.Set(detail, count).ShouldBe(expected);

    [Theory]
    [InlineData("Failed.", "x [kept-aside=2]", 2)]
    [InlineData("Failed. [kept-aside=5]", "x [kept-aside=2]", 5)]
    [InlineData("Failed.", "no mark", 0)]
    [InlineData(null, null, 0)]
    public void Carry_keeps_the_larger_count(string? detail, string? previous, int expected)
        => KeptAsideDetail.TryRead(KeptAsideDetail.Carry(detail, previous)).ShouldBe(expected);
}
