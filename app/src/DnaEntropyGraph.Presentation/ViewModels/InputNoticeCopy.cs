using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>Which <c>Resources.resw</c> string tells the user about a validation notice (Hard Rule 13). A Guards test checks every code.</summary>
public static class InputNoticeCopy
{
    /// <summary>The key to show for a notice that mentions <paramref name="count"/> things: the singular copy (<c>..._One</c>) when it is exactly one and the copy counts something, else <see cref="KeyFor(InputNoticeCode)"/>.</summary>
    public static string? KeyFor(InputNoticeCode code, long count) => count == 1
        ? code switch
        {
            InputNoticeCode.EmptyHeaders => "NewRunNotice_EmptyHeaders_One",
            InputNoticeCode.RepeatedIds => "NewRunNotice_RepeatedIds_One",
            InputNoticeCode.RnaConverted => "NewRunNotice_RnaConverted_One",
            InputNoticeCode.AmbiguityMasked => "NewRunNotice_AmbiguityMasked_One",
            InputNoticeCode.AmbiguityKept => "NewRunNotice_AmbiguityKept_One",
            InputNoticeCode.ShortSequence => "NewRunNotice_ShortSequence_One",
            InputNoticeCode.DigitsRemoved => "NewRunNotice_DigitsRemoved_One",
            _ => KeyFor(code),
        }
        : KeyFor(code);

    /// <summary>The resource key for <paramref name="code"/>, or null when the pill already says it (the record and gene counts).</summary>
    public static string? KeyFor(InputNoticeCode code) => code switch
    {
        InputNoticeCode.RecordsRead or InputNoticeCode.GenBankRead => null,
        InputNoticeCode.RecordSkippedNoSequence => "NewRunNotice_RecordSkippedNoSequence",
        InputNoticeCode.EmptyHeaders => "NewRunNotice_EmptyHeaders",
        InputNoticeCode.RepeatedIds => "NewRunNotice_RepeatedIds",
        InputNoticeCode.GenBankRecordSkippedNoSequence => "NewRunNotice_GenBankRecordSkippedNoSequence",
        InputNoticeCode.RnaConverted => "NewRunNotice_RnaConverted",
        InputNoticeCode.AmbiguityMasked => "NewRunNotice_AmbiguityMasked",
        InputNoticeCode.AmbiguityKept => "NewRunNotice_AmbiguityKept",
        InputNoticeCode.ShortSequence => "NewRunNotice_ShortSequence",
        InputNoticeCode.LeadingHeaderIgnored => "NewRunNotice_LeadingHeaderIgnored",
        InputNoticeCode.DigitsRemoved => "NewRunNotice_DigitsRemoved",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped InputNoticeCode: add it to InputNoticeCopy and Resources.resw."),
    };
}

/// <summary>What a drop onto the New run page carried: the real paths, how many items had no path (a file inside a zip, an e-mail attachment), and whether reading the drop failed.</summary>
public sealed record DroppedItems(IReadOnlyList<string> Paths, int SkippedVirtual, bool Failed);
