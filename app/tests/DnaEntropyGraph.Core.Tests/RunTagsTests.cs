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

    [Fact]
    public void A_tag_cut_at_the_limit_never_splits_a_surrogate_pair_and_still_serializes()
    {
        // 39 letters, then a 2-char emoji straddling index 40.
        var typed = new string('a', RunTags.MaxTagLength - 1) + "😀" + "tail";

        var tags = RunTags.ParseInput(typed);

        tags.Count.ShouldBe(1);
        tags[0].Length.ShouldBeLessThanOrEqualTo(RunTags.MaxTagLength);
        char.IsHighSurrogate(tags[0][^1]).ShouldBeFalse();
        Should.NotThrow(() => RunTags.Serialize(tags));
    }

    [Fact]
    public void A_note_over_the_limit_is_cut_at_the_limit_without_splitting_a_surrogate_pair()
    {
        var typed = new string('n', RunTags.MaxNotesLength - 1) + "😀" + "tail";

        var clamped = RunTags.ClampNotes(typed)!;

        clamped.Length.ShouldBeLessThanOrEqualTo(RunTags.MaxNotesLength);
        char.IsHighSurrogate(clamped[^1]).ShouldBeFalse();
        RunTags.ClampNotes("short").ShouldBe("short");
        RunTags.ClampNotes(null).ShouldBeNull();
    }
}
