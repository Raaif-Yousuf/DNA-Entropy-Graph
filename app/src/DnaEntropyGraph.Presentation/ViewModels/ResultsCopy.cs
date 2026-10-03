namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Every <c>Resources.resw</c> key the Results page's ViewModel reads (Hard Rule 13). Plain, non-dotted keys,
/// because they are read from code. <see cref="AllKeys"/> is what the guard checks against the .resw.
/// </summary>
public static class ResultsCopy
{
    public const string NoRun = "Results_NoRun";
    public const string FolderMissing = "Results_FolderMissing";
    public const string ReadFailed = "Results_ReadFailed";
    public const string StatsNone = "Results_StatsNone";
    public const string StatsUnreadable = "Results_StatsUnreadable";
    public const string FileActionFailed = "Results_FileActionFailed";
    public const string OpenFolderFailed = "Results_OpenFolderFailed";
    public const string CopyFailed = "Results_CopyFailed";

    // The worker's direction tokens (DirectionKey maps a token to one of these).
    public const string DirectionBothCombined = "Results_Direction_BothCombined";
    public const string DirectionBothAveraged = "Results_Direction_BothAveraged";
    public const string DirectionBothSeparate = "Results_Direction_BothSeparate";
    public const string DirectionForwardOnly = "Results_Direction_ForwardOnly";
    public const string DirectionReverseOnly = "Results_Direction_ReverseOnly";
    public const string DirectionUnknown = "Results_Direction_Unknown";

    // Format strings: {0:F3} bits and so on.
    public const string Headline = "Results_Headline";
    public const string Bits = "Results_Bits";
    public const string Length = "Results_Length";
    public const string SizeBytes = "Results_Size_Bytes";
    public const string SizeKilobytes = "Results_Size_Kilobytes";
    public const string SizeMegabytes = "Results_Size_Megabytes";
    public const string SizeUnknown = "Results_Size_Unknown";

    /// <summary>
    /// The copy key for a direction token as <c>writers/summary.py</c> writes it (<c>Direction</c> values in <c>worker/.../config.py</c>),
    /// or <see cref="DirectionUnknown"/> for a token this app does not know.
    /// </summary>
    public static string DirectionKey(string token) => token switch
    {
        "both-combined" => DirectionBothCombined,
        "both-averaged" => DirectionBothAveraged,
        "both-separate" => DirectionBothSeparate,
        "forward-only" => DirectionForwardOnly,
        "reverse-only" => DirectionReverseOnly,
        _ => DirectionUnknown,
    };

    /// <summary>Every key above, for the guard that proves each one exists in the .resw.</summary>
    public static IReadOnlyList<string> AllKeys { get; } =
    [
        NoRun, FolderMissing, ReadFailed, StatsNone, StatsUnreadable, FileActionFailed, OpenFolderFailed, CopyFailed,
        DirectionBothCombined, DirectionBothAveraged, DirectionBothSeparate, DirectionForwardOnly, DirectionReverseOnly, DirectionUnknown,
        Headline, Bits, Length, SizeBytes, SizeKilobytes, SizeMegabytes, SizeUnknown,
    ];

    /// <summary>The keys whose text is a format string and must carry its placeholders.</summary>
    public static IReadOnlyList<string> FormatKeys { get; } = [StatsUnreadable, DirectionUnknown, Headline, Bits, Length, SizeBytes, SizeKilobytes, SizeMegabytes];
}
