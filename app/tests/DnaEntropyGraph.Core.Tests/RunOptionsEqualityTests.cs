using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #552: RunOptions is a record compared by value (a rerun is asserted and de-duplicated by it); its list member must compare by items, not by reference.</summary>
public sealed class RunOptionsEqualityTests
{
    private static RunOptions With(IReadOnlyList<int> windows) => new() { ModelId = "evo2_7b", RunTarget = "cloud", SmoothingWindows = windows };

    [Fact]
    public void Two_option_sets_with_the_same_smoothing_windows_in_different_list_instances_are_equal_with_equal_hash_codes()
    {
        var a = With([11, 101]);
        var b = With(new List<int> { 11, 101 });

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Theory]
    [InlineData(new[] { 11, 102 })]
    [InlineData(new[] { 101, 11 })]
    [InlineData(new[] { 11 })]
    [InlineData(new int[0])]
    public void Different_smoothing_windows_are_not_equal(int[] other)
        => With([11, 101]).ShouldNotBe(With(other));

    [Fact]
    public void A_difference_in_another_member_is_still_seen()
        => (With([51]) with { Seed = 3 }).ShouldNotBe(With([51]));

    [Fact]
    public void A_json_round_trip_equals_the_original_including_non_default_windows()
    {
        var original = With([11, 101]) with { Seed = 9, OutputFolder = @"D:\lab" };

        RunOptionsJson.TryDeserialize(RunOptionsJson.Serialize(original)).ShouldBe(original);
        RunOptionsJson.TryDeserialize(RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud" }))
            .ShouldBe(new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud" });
    }
}
