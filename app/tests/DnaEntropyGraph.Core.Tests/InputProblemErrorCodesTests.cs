using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Hard Rule 2 / #479: every way a staged input can be refused locally maps to one recorded run error code.</summary>
public class InputProblemErrorCodesTests
{
    private static readonly Dictionary<InputProblemCode, string> Expected = new()
    {
        [InputProblemCode.FileUnreadable] = "input_missing",
        [InputProblemCode.NoRecords] = "input_empty",
        [InputProblemCode.EmptySequence] = "input_empty",
        [InputProblemCode.FileTooLarge] = "input_too_long",
        [InputProblemCode.RecordTooLong] = "input_too_long",
        [InputProblemCode.TotalTooLong] = "input_too_long",
        [InputProblemCode.BatchBudgetExceeded] = "input_too_long",
        [InputProblemCode.InvalidCharacter] = "input_invalid_character",
        [InputProblemCode.AmbiguityRefused] = "input_ambiguity_refused",
        [InputProblemCode.RnaNotAllowed] = "input_rna",
        [InputProblemCode.UndecodableText] = "input_not_utf8",
    };

    public static TheoryData<InputProblemCode> AllKinds()
    {
        var data = new TheoryData<InputProblemCode>();
        foreach (var kind in Enum.GetValues<InputProblemCode>())
        {
            data.Add(kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_problem_kind_maps_to_a_known_code_with_the_expected_value(InputProblemCode kind)
    {
        Expected.ShouldContainKey(kind, "a new InputProblemCode needs a row here and a mapping in InputProblemErrorCodes");

        var code = InputProblemErrorCodes.For(kind);

        code.ShouldBe(Expected[kind]);
        RunErrorCodes.All.ShouldContain(code);
    }

    [Fact]
    public void The_detail_names_the_code_record_and_position_and_never_the_problem_text()
    {
        var problem = new InputProblem(InputProblemCode.InvalidCharacter, 2, 7, "character 'X' in record 'secretname'");

        var detail = InputProblemErrorCodes.DetailFor(problem);

        detail.ShouldBe("InvalidCharacter record=2 position=7");
        detail.ShouldNotContain("secretname");
    }
}
