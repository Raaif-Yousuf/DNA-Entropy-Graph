using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #141: a whole <see cref="RunRecord"/> can end up in a log line or a failure message (<c>$"{run}"</c>), and a record's own ToString prints every
/// member. The user's notes and tags must never reach that text. The app has no Serilog destructuring (MEASURED 2026-10-29: no <c>using Serilog</c>
/// and no <c>{@...}</c> template in app/src), so ToString is the only path that prints a record's members.
/// </summary>
public sealed class RunRecordToStringTests
{
    private static readonly RunRecord Run = new("20261001-080000-abc234", JobPhase.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        Notes: "zebra-batch-42 note text", TagsJson: "[\"clone7-secret\",\"redo\"]");

    [Fact]
    public void ToString_prints_neither_the_notes_nor_the_tags()
    {
        var text = Run.ToString();

        text.ShouldNotContain("zebra-batch-42");
        text.ShouldNotContain("clone7-secret");
        text.ShouldNotContain("redo");
        text.ShouldContain("<notes withheld>");
        text.ShouldContain("<tags withheld>");
    }

    [Fact]
    public void ToString_prints_neither_the_run_name_nor_the_output_folder_nor_the_options_nor_the_manifest()
    {
        // Issue #1046: Name now carries the input file's stem, and the options, manifest and output folder name the file too (never log the file name).
        var text = (Run with
        {
            Name = "patient_42_evo2_7b",
            OutputDir = @"C:\Users\someone\Downloads\patient_42_evo2_7b",
            OptionsJson = "{\"InputPath\":\"C:\\\\data\\\\patient_42.gb\"}",
            ManifestJson = "{\"inputs\":[{\"name\":\"patient_42.gb\"}]}",
        }).ToString();

        text.ShouldNotContain("patient_42");
        text.ShouldNotContain("someone");
        text.ShouldContain("<name withheld>");
        text.ShouldContain("<output folder withheld>");
        text.ShouldContain("<options withheld>");
        text.ShouldContain("<manifest withheld>");
    }

    [Fact]
    public void ToString_still_prints_the_other_members_and_a_run_without_notes_shows_null()
    {
        var text = (Run with { Notes = null, TagsJson = null }).ToString();

        text.ShouldContain("JobId = 20261001-080000-abc234");
        text.ShouldContain("Phase = Completed");
        text.ShouldContain("Notes = null");
        text.ShouldContain("TagsJson = null");
        text.ShouldNotContain("withheld");
    }

    [Fact]
    public void Equality_and_with_expressions_are_not_affected_by_the_print_override()
    {
        (Run with { Notes = "other" }).ShouldNotBe(Run);
        (Run with { }).ShouldBe(Run);
        Run.Notes.ShouldBe("zebra-batch-42 note text");
    }
}
