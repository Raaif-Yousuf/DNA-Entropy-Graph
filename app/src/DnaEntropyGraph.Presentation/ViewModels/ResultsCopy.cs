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

    // Issue #118: the entropy overview strip.
    public const string ExportPngFailed = "Results_ExportPngFailed";
    public const string ExportPngTitle = "Results_ExportPng_Title";
    public const string ExportPngXAxis = "Results_ExportPng_XAxis";
    public const string ExportPngYAxis = "Results_ExportPng_YAxis";

    // Issue #552: the gene table.
    public const string GenesTruncated = "Results_GenesTruncated";
    public const string GenesUnreadable = "Results_GenesUnreadable";
    public const string GenesRowsSkipped = "Results_GenesRowsSkipped";
    public const string GenesOrderFile = "Results_GenesOrderFile";
    public const string GenesOrderSorted = "Results_GenesOrderSorted";
    public const string GenesSortedAscending = "Results_GenesSortedAscending";
    public const string GenesSortedDescending = "Results_GenesSortedDescending";

    /// <summary>The sort keys of <see cref="ResultsGeneTable"/>, in column order.</summary>
    public static IReadOnlyList<string> GeneColumnKeys { get; } = ["contig", "gene", "begin", "end", "strand", "length", "mean", "min", "max", "low"];

    /// <summary>The header text key of a gene table column.</summary>
    public static string GeneColumnKey(string sortKey) => "Results_GeneColumn_" + sortKey;

    // Issue #586: Open in IGV and Open in Geneious.
    public const string IgvNoFiles = "Results_IgvNoFiles";
    public const string IgvNotFound = "Results_IgvNotFound";
    public const string IgvLaunchFailed = "Results_IgvLaunchFailed";
    public const string IgvRejected = "Results_IgvRejected";
    public const string IgvNoReply = "Results_IgvNoReply";
    public const string IgvNoAnswer = "Results_IgvNoAnswer";
    public const string ViewerUnsafePath = "Results_ViewerUnsafePath";
    public const string IgvPathUnsendable = "Results_IgvPathUnsendable";
    public const string IgvNoGenome = "Results_IgvNoGenome";
    public const string IgvFirstInputOnly = "Results_IgvFirstInputOnly";
    public const string GeneiousNoFiles = "Results_GeneiousNoFiles";
    public const string GeneiousNotFound = "Results_GeneiousNotFound";
    public const string GeneiousLaunchFailed = "Results_GeneiousLaunchFailed";

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
        NoRun, FolderMissing, ReadFailed, StatsNone, StatsUnreadable, FileActionFailed, OpenFolderFailed, CopyFailed, ExportPngFailed,
        IgvNoFiles, IgvNotFound, IgvLaunchFailed, IgvRejected, IgvNoReply, IgvNoAnswer, ViewerUnsafePath, IgvPathUnsendable, IgvNoGenome, IgvFirstInputOnly,
        GeneiousNoFiles, GeneiousNotFound, GeneiousLaunchFailed,
        DirectionBothCombined, DirectionBothAveraged, DirectionBothSeparate, DirectionForwardOnly, DirectionReverseOnly, DirectionUnknown,
        Headline, Bits, Length, SizeBytes, SizeKilobytes, SizeMegabytes, SizeUnknown,
        ExportPngTitle, ExportPngXAxis, ExportPngYAxis, GenesTruncated, GenesUnreadable, GenesRowsSkipped, GenesOrderFile, GenesOrderSorted, GenesSortedAscending, GenesSortedDescending,
        .. GeneColumnKeys.Select(GeneColumnKey),
    ];

    /// <summary>The keys whose text is a format string and must carry its placeholders.</summary>
    public static IReadOnlyList<string> FormatKeys { get; } = [ExportPngTitle, GenesTruncated, GenesRowsSkipped, GenesOrderSorted, GenesUnreadable, GenesSortedAscending, GenesSortedDescending, StatsUnreadable, DirectionUnknown, Headline, Bits, Length, SizeBytes, SizeKilobytes, SizeMegabytes];
}

