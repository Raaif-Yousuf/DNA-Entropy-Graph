namespace DnaEntropyGraph.Presentation.Viewer;

/// <summary>Why the viewer page shows an error instead of the genome.</summary>
public enum ViewerFailure
{
    NoRun,
    RuntimeMissing,
    FolderMissing,
    NoSequenceFile,
    NoEntropyTrack,
    AmbiguousSequence,
    SequenceTooLarge,
    ViewerError,
    ProcessFailed,
    InitFailed,
}

/// <summary>The .resw keys for one viewer error. <see cref="ActionKey"/> is null when the error has no button.</summary>
public readonly record struct ViewerCopyKeys(string TitleKey, string BodyKey, string? ActionKey);

/// <summary>
/// Every viewer error key as a literal, so scripts/check_app_wiring.py can see each one is looked up
/// (a key built as <c>prefix + "_Title"</c> looks orphaned to it, and to a reader grepping for the key).
/// </summary>
public static class ViewerCopy
{
    public static ViewerCopyKeys For(ViewerFailure failure) => failure switch
    {
        ViewerFailure.NoRun => new("ViewerNoRun_Title", "ViewerNoRun_Body", null),
        ViewerFailure.RuntimeMissing => new("ViewerRuntimeMissing_Title", "ViewerRuntimeMissing_Body", "ViewerRuntimeMissing_Action"),
        ViewerFailure.FolderMissing => new("ViewerFolderMissing_Title", "ViewerFolderMissing_Body", null),
        ViewerFailure.NoSequenceFile => new("ViewerNoSequenceFile_Title", "ViewerNoSequenceFile_Body", null),
        ViewerFailure.NoEntropyTrack => new("ViewerNoEntropyTrack_Title", "ViewerNoEntropyTrack_Body", null),
        ViewerFailure.AmbiguousSequence => new("ViewerAmbiguousSequence_Title", "ViewerAmbiguousSequence_Body", null),
        ViewerFailure.SequenceTooLarge => new("ViewerSequenceTooLarge_Title", "ViewerSequenceTooLarge_Body", null),
        ViewerFailure.ViewerError => new("ViewerError_Title", "ViewerError_Body", "ViewerError_Action"),
        ViewerFailure.ProcessFailed => new("ViewerProcessFailed_Title", "ViewerProcessFailed_Body", null),
        ViewerFailure.InitFailed => new("ViewerInitFailed_Title", "ViewerInitFailed_Body", null),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "No viewer copy for this failure."),
    };
}
