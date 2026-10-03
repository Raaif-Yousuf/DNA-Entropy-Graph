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

    // Format strings: {0:F3} bits and so on.
    public const string Headline = "Results_Headline";
    public const string Bits = "Results_Bits";
    public const string Length = "Results_Length";
    public const string SizeBytes = "Results_Size_Bytes";
    public const string SizeKilobytes = "Results_Size_Kilobytes";
    public const string SizeMegabytes = "Results_Size_Megabytes";

    /// <summary>Every key above, for the guard that proves each one exists in the .resw.</summary>
    public static IReadOnlyList<string> AllKeys { get; } =
    [
        NoRun, FolderMissing, ReadFailed, StatsNone, StatsUnreadable, FileActionFailed, OpenFolderFailed, CopyFailed,
        Headline, Bits, Length, SizeBytes, SizeKilobytes, SizeMegabytes,
    ];

    /// <summary>The keys whose text is a format string and must carry its placeholders.</summary>
    public static IReadOnlyList<string> FormatKeys { get; } = [StatsUnreadable, Headline, Bits, Length, SizeBytes, SizeKilobytes, SizeMegabytes];
}
