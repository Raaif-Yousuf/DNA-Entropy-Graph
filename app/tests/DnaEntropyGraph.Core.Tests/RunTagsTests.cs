using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #141: the tags of a run are typed as one line and stored as a JSON array in the Runs.TagsJson column.</summary>
public sealed class RunTagsTests
{
    [Fact]
    public void Input_is_split_on_commas_semicolons_and_newlines_trimmed_and_deduplicated_ignoring_case()
    {
        RunTags.ParseInput(" failed clone ,Plasmid;plasmid\nQC,, ").ShouldBe(["failed clone", "Plasmid", "QC"]);
    }

    [Fact]
    public void Empty_or_blank_input_has_no_tags()
    {
        RunTags.ParseInput(null).ShouldBeEmpty();
        RunTags.ParseInput("  , ;").ShouldBeEmpty();
    }

    [Fact]
    public void A_tag_is_cut_to_the_length_limit_and_the_list_to_the_count_limit()
    {
        RunTags.ParseInput(new string('a', RunTags.MaxTagLength + 10))[0].Length.ShouldBe(RunTags.MaxTagLength);
        RunTags.ParseInput(string.Join(',', Enumerable.Range(0, RunTags.MaxTags + 5))).Count.ShouldBe(RunTags.MaxTags);
    }

    [Fact]
    public void Serialize_then_parse_round_trips_and_no_tags_is_null()
    {
        var json = RunTags.Serialize(["a", "b c"]);
        json.ShouldBe("""["a","b c"]""");
        RunTags.Parse(json).ShouldBe(["a", "b c"]);
        RunTags.Serialize([]).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void Unreadable_stored_tags_read_as_none_never_throw(string? stored)
    {
        RunTags.Parse(stored).ShouldBeEmpty();
    }

    [Fact]
    public void Format_joins_with_comma_and_space()
    {
        RunTags.Format(["a", "b"]).ShouldBe("a, b");
    }
}
