using DnaEntropyGraph.Core;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #63: the run/batch name template, its sanitisation, and the _2 collision rule.</summary>
public sealed class RunNamerTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 14, 7, 0);

    private static string Name(string template, string file = @"C:\data\SetTnpB.gb", int n = 1, params string[] existing)
        => RunNamer.Resolve(template, file, "evo2_7b", Now, n, existing);

    [Fact]
    public void The_default_template_is_the_input_file_name_without_folder_or_extension()
        => Name("{file}").ShouldBe("SetTnpB");

    [Fact]
    public void Every_token_is_expanded()
        => Name("{file}_{date:yyyy-MM-dd}_{time:HHmm}_{model}_{n}", n: 3).ShouldBe("SetTnpB_2026-10-03_1407_evo2_7b_3");

    [Fact]
    public void Date_and_time_tokens_default_to_their_documented_formats()
        => Name("{date} {time}").ShouldBe("2026-10-03 1407");

    [Fact]
    public void A_pasted_sequence_with_no_file_is_named_pasted()
        => RunNamer.Resolve("{file}", null, "evo2_7b", Now, 1, []).ShouldBe("pasted");

    [Theory]
    [InlineData("a/b", "a_b")]
    [InlineData("what?*<>|\"x", "what______x")]
    [InlineData("trailing. . ", "trailing")]
    public void Characters_Windows_forbids_in_a_folder_name_become_underscores(string template, string expected)
        => Name(template).ShouldBe(expected);

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9.txt")]
    public void A_reserved_device_name_is_not_used_as_is(string template)
    {
        var name = Name(template);
        name.ShouldStartWith("run_");
        name.ShouldEndWith(template);
    }

    [Fact]
    public void An_empty_result_falls_back_to_run()
    {
        Name("").ShouldBe("run");
        Name("  ").ShouldBe("run");
        Name("...").ShouldBe("run");
    }

    [Fact]
    public void An_unknown_token_is_kept_literally_so_the_user_sees_the_typo()
        => Name("{flie}").ShouldBe("{flie}");

    [Fact]
    public void A_bad_date_format_does_not_throw()
        => Should.NotThrow(() => Name("{date:QQQQ}"));

    [Fact]
    public void A_very_long_name_is_capped_at_100_characters()
        => Name(new string('a', 400)).Length.ShouldBe(100);

    [Fact]
    public void A_name_already_used_gets_2_appended()
        => Name("{file}", existing: "SetTnpB").ShouldBe("SetTnpB_2");

    [Fact]
    public void Collisions_continue_to_3_and_compare_ignoring_case()
        => Name("{file}", existing: ["settnpb", "SetTnpB_2"]).ShouldBe("SetTnpB_3");

    [Fact]
    public void A_name_nobody_uses_is_left_alone()
        => Name("{file}", existing: ["Other"]).ShouldBe("SetTnpB");

    [Fact]
    public void A_collision_suffix_never_pushes_the_name_over_the_cap()
    {
        var name = Name(new string('a', 400), existing: new string('a', 100));
        name.Length.ShouldBeLessThanOrEqualTo(100);
        name.ShouldEndWith("_2");
    }

    [Fact]
    public void A_file_name_with_a_dotted_stem_keeps_everything_before_the_last_dot()
        => Name("{file}", file: @"C:\x\a.b.fasta").ShouldBe("a.b");
}
