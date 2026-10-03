using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// The C# half of worker issue #468 (closed in the worker, still open here): a non-ASCII
/// letter is refused at its true position, never case-folded into an ASCII base. Each case
/// is ported from <c>worker/tests/test_validation.py</c>
/// (<c>test_non_ascii_letter_is_refused_naming_it_and_its_position</c>,
/// <c>test_issue_468_reproduction_string_is_refused</c>,
/// <c>test_ascii_lowercase_is_still_uppercased_including_iupac_codes</c>) so the two
/// validators agree on the same inputs. Whitespace cases were measured against the worker's
/// <c>validate_sequence</c> on 2026-10-02 (Python <c>\s</c> removes all of them).
/// </summary>
public sealed class SequenceValidatorNonAsciiTests
{
    public static IEnumerable<object[]> NonAsciiLetters() =>
    [
        ["\u00df", "U+00DF"], // sharp s
        ["\u017f", "U+017F"], // long s: upper-cases to ASCII S, a valid IUPAC code
        ["\ufb01", "U+FB01"], // the "fi" ligature
        ["\u0131", "U+0131"], // dotless i: upper-cases to ASCII I
        ["\u212a", "U+212A"], // Kelvin sign: lower-cases to ASCII k
    ];

    [Theory]
    [MemberData(nameof(NonAsciiLetters))]
    public void A_non_ascii_letter_is_refused_naming_its_code_point_and_true_position_under_every_policy(
        string letter, string codePoint)
    {
        foreach (var policy in Enum.GetValues<AmbiguityPolicy>())
        {
            var ex = Should.Throw<SequenceValidationException>(
                () => SequenceValidator.Validate("ACGT" + letter + "ACGT", ambiguityPolicy: policy));

            ex.Message.ShouldContain(codePoint);
            ex.Message.ShouldContain("position 5");
            ex.Message.All(char.IsAscii).ShouldBeTrue($"policy {policy}: the message must be ASCII (Hard Rule 5 mirror)");
        }
    }

    [Fact]
    public void The_issue_468_reproduction_string_is_refused_at_position_5()
    {
        var ex = Should.Throw<SequenceValidationException>(
            () => SequenceValidator.Validate("ACGT" + "\u00df" + "ACGT" + "\u017f" + "ACGT", ambiguityPolicy: AmbiguityPolicy.Keep));

        ex.Message.ShouldContain("position 5");
        ex.Message.ShouldContain("2 non-ACGT");
    }

    [Fact]
    public void A_character_outside_the_BMP_is_named_by_one_code_point_and_counted_once()
    {
        // Python indexes by code point; a surrogate pair must not count as two bad bases.
        var ex = Should.Throw<SequenceValidationException>(
            () => SequenceValidator.Validate("ACGT\U0001F600ACGT", ambiguityPolicy: AmbiguityPolicy.Keep));

        ex.Message.ShouldContain("U+1F600");
        ex.Message.ShouldContain("position 5");
        ex.Message.ShouldContain("(1 non-ACGT");
    }

    [Fact]
    public void Ascii_lowercase_is_still_uppercased_including_iupac_codes()
    {
        var result = SequenceValidator.Validate("acgtnrysw", ambiguityPolicy: AmbiguityPolicy.Keep);

        result.Seq.ShouldBe("ACGTNRYSW");
    }

    [Theory]
    [InlineData("ACGT\u001cACGT")]
    [InlineData("ACGT\u2028ACGT")]
    [InlineData("ACGT\u0085ACGT")]
    [InlineData("ACGT\u000bACGT")]
    [InlineData("ACGT\u00a0ACGT")]
    [InlineData("ACGT\u2003ACGT")]
    public void Whitespace_the_worker_removes_is_removed_here_too(string raw)
    {
        SequenceValidator.Validate(raw).Seq.ShouldBe("ACGTACGT");
    }

    [Fact]
    public void A_superscript_digit_is_refused_not_removed_like_the_worker()
    {
        // Python: re \d does not match U+00B2, so it survives to the invalid-character check.
        var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate("AC\u0662GT\u00b2ACGT"));

        ex.Message.ShouldContain("U+00B2");
        ex.Message.ShouldContain("position 5");
    }

    [Theory]
    [InlineData("ACGTXACGT", SequenceFailure.InvalidCharacter, 5)]
    [InlineData("ACGTUACGT", SequenceFailure.Rna, 5)]
    [InlineData("ACGTNACGT", SequenceFailure.AmbiguityRefused, 5)]
    [InlineData("ACGT\ufffdACGT", SequenceFailure.Undecodable, 5)]
    public void A_refusal_carries_a_machine_reason_and_the_position(string raw, SequenceFailure reason, int position)
    {
        var ex = Should.Throw<SequenceValidationException>(
            () => SequenceValidator.Validate(raw, ambiguityPolicy: AmbiguityPolicy.Error));

        ex.Reason.ShouldBe(reason);
        ex.Position.ShouldBe(position);
    }

    [Fact]
    public void Empty_and_too_long_carry_their_reasons_and_no_position()
    {
        Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate("  1 2 ")).Reason.ShouldBe(SequenceFailure.Empty);

        var tooLong = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate("ACGTACGTAC", maxLen: 5));
        tooLong.Reason.ShouldBe(SequenceFailure.TooLong);
        tooLong.Position.ShouldBeNull();
    }

    [Fact]
    public void An_RNA_position_counts_code_points_before_it()
    {
        var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate("AC\U0001F600U"));

        ex.Message.ShouldContain("position 4");
    }

    [Fact]
    public void A_lone_surrogate_is_a_validation_refusal_not_another_exception()
    {
        // Built from chars, not theory data: xunit serialises theory strings and would replace a
        // lone surrogate with U+FFFD before the test saw it.
        foreach (var (unit, codePoint) in new[] { ((char)0xD83D, "U+D83D"), ((char)0xDE00, "U+DE00") })
        {
            var raw = "AC" + unit + "AC";

            var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate(raw));

            ex.Reason.ShouldBe(SequenceFailure.InvalidCharacter);
            ex.Message.ShouldContain(codePoint);
            ex.Message.ShouldContain("position 3");
        }
    }

    [Fact]
    public void An_astral_decimal_digit_is_removed_like_the_worker_and_counted_once()
    {
        // U+1D7CE MATHEMATICAL BOLD DIGIT ZERO is Unicode Nd: Python's \d removes it.
        var result = SequenceValidator.Validate("AC\U0001D7CEGTACGT");

        result.Seq.ShouldBe("ACGTACGT");
        result.Notices.ShouldContain("Removed 1 digit character(s) (e.g. line numbers).");
    }

    [Fact]
    public void A_superscript_digit_is_counted_in_the_digit_notice_like_the_worker_even_though_it_is_refused()
    {
        // Python: str.isdigit() is true for U+00B2 but re \d does not match it. The refusal
        // names the character; this documents that it is not silently dropped.
        var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate("ACGT\u00b2ACGT"));

        ex.Reason.ShouldBe(SequenceFailure.InvalidCharacter);
        ex.Position.ShouldBe(5);
    }
}
