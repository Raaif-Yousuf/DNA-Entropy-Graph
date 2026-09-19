using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Neighbour tests around the fixture-driven <see cref="SequenceValidatorParityTests" />:
/// the shapes the CLI parity fixture does not happen to exercise (no case there hits an
/// ambiguity code, an invalid character, an empty input, or a too-long sequence), covering
/// this lane's brief: "ambiguity/illegal-character rules, empty and whitespace-only input,
/// ... the length/short-input rules."
/// </summary>
public sealed class SequenceValidatorTests
{
    [Fact]
    public void Empty_input_is_rejected_with_INPUT_INVALID()
    {
        var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate(""));
        ex.Message.ShouldBe("No nucleotides found after cleaning the input (empty sequence).");
    }

    [Fact]
    public void Whitespace_only_input_is_rejected_as_empty()
    {
        var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate("   \n\t  \r\n  "));
        ex.Message.ShouldBe("No nucleotides found after cleaning the input (empty sequence).");
    }

    [Fact]
    public void An_ambiguity_code_is_refused_under_the_Error_policy_by_default()
    {
        var ex = Should.Throw<SequenceValidationException>(
            () => SequenceValidator.Validate("ACGTNACGTACGT", minLen: 0));

        ex.Message.ShouldBe(
            "Ambiguity code 'N' at position 5 (1 total: N). This run's ambiguity policy is "
            + "'error' (refuse). Choose 'keep' or 'mask' to run anyway, or clean the input to plain A/C/G/T.");
    }

    [Fact]
    public void Kept_ambiguity_codes_are_left_in_place_with_a_notice()
    {
        var result = SequenceValidator.Validate("ACGTNACGTACGT", minLen: 0, ambiguityPolicy: AmbiguityPolicy.Keep);

        result.Seq.ShouldBe("ACGTNACGTACGT");
        result.Notices.ShouldContain(
            "Kept 1 ambiguity code(s) (N); entropy at those positions reflects the model's "
            + "prediction for that exact code, not a definite base (docs/science_and_formats.md).");
    }

    [Fact]
    public void Masked_ambiguity_codes_are_normalised_to_N_with_a_notice()
    {
        var result = SequenceValidator.Validate("ACGTRACGTACGT", minLen: 0, ambiguityPolicy: AmbiguityPolicy.Mask);

        result.Seq.ShouldBe("ACGTNACGTACGT"); // R -> N
        result.Notices.ShouldContain(
            "Masked 1 ambiguity code(s) (R) to 'N'; entropy at those positions reflects the "
            + "model's prediction for 'N', not the original code (docs/science_and_formats.md).");
    }

    [Fact]
    public void A_genuinely_invalid_character_is_always_an_error_regardless_of_ambiguity_policy()
    {
        var ex = Should.Throw<SequenceValidationException>(
            () => SequenceValidator.Validate("ACGTXACGTACGT", minLen: 0, ambiguityPolicy: AmbiguityPolicy.Keep));

        ex.Message.ShouldBe("Invalid character 'X' at position 5 (1 non-ACGT character(s) total). Only A, C, G, T are allowed.");
    }

    [Fact]
    public void A_replacement_character_is_diagnosed_as_an_encoding_problem_not_a_wrong_base()
    {
        var badlyDecoded = "ACGT" + '�' + "ACGTACGT";

        var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate(badlyDecoded, minLen: 0));

        ex.Message.ShouldBe(
            "Found 1 character(s) that could not be decoded as text (position 5 is the first), "
            + "which usually means the file was not saved as UTF-8 (e.g. Windows-1252 or another codepage). "
            + "Re-save the file with UTF-8 encoding and try again.");
    }

    [Fact]
    public void A_sequence_over_the_cap_is_rejected()
    {
        var seq = new string('A', 20);
        var ex = Should.Throw<SequenceValidationException>(() => SequenceValidator.Validate(seq, maxLen: 10, minLen: 0));

        ex.Message.ShouldBe(
            "Sequence length 20 exceeds the single-pass cap of 10 nt. Longer loci need "
            + "windowing (future work); raise --max-len only if the GPU has the VRAM.");
    }

    [Fact]
    public void A_leading_FASTA_header_line_is_stripped_with_a_notice_never_leaking_its_text()
    {
        var result = SequenceValidator.Validate(">my secret plasmid name\nACGTACGTACGT", minLen: 0);

        result.Seq.ShouldBe("ACGTACGTACGT");
        result.Notices.ShouldContain(n => n.StartsWith("Ignored a leading FASTA header line", StringComparison.Ordinal));
        result.Notices.ShouldAllBe(n => !n.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_multi_record_style_paste_is_not_this_functions_job_it_only_sees_one_sequence()
    {
        // validate_sequence (and this port) always cleans exactly ONE sequence - multi-record
        // handling is FastaLite/GenBankLite's job, upstream of this call, matching the worker.
        var result = SequenceValidator.Validate("acgt\nACGT", minLen: 0);
        result.Seq.ShouldBe("ACGTACGT");
    }
}
