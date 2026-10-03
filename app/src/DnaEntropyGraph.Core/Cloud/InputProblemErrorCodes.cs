using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Maps what <see cref="InputFileValidator"/> found wrong with a staged input to the run error
/// code recorded for it (Hard Rule 2, #479). The user text lives in Resources.resw under
/// <see cref="RunErrorCodes.ResourceKey"/>.
/// </summary>
public static class InputProblemErrorCodes
{
    public static string For(InputProblemCode kind) => kind switch
    {
        InputProblemCode.FileUnreadable => RunErrorCodes.InputMissing,
        InputProblemCode.NoRecords or InputProblemCode.EmptySequence => RunErrorCodes.InputEmpty,
        InputProblemCode.FileTooLarge or InputProblemCode.RecordTooLong or InputProblemCode.TotalTooLong or InputProblemCode.BatchBudgetExceeded => RunErrorCodes.InputTooLong,
        InputProblemCode.InvalidCharacter => RunErrorCodes.InputInvalidCharacter,
        InputProblemCode.AmbiguityRefused => RunErrorCodes.InputAmbiguityRefused,
        InputProblemCode.RnaNotAllowed => RunErrorCodes.InputRna,
        InputProblemCode.UndecodableText => RunErrorCodes.InputNotUtf8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped InputProblemCode: add it to InputProblemErrorCodes."),
    };

    /// <summary>
    /// The text stored in <c>RunRecord.ErrorDetail</c>: the code, record index and position only.
    /// Never <see cref="InputProblem.Detail"/>, which can hold sequence text or a record name.
    /// </summary>
    public static string DetailFor(InputProblem problem)
        => $"{problem.Code} record={problem.RecordIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} position={problem.Position?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";
}
