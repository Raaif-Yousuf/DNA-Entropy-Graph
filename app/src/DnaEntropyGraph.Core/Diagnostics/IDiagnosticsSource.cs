namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// The app data folder (<c>%LOCALAPPDATA%\DNAEntropyGraph\</c>) as the diagnostics builder sees it: a flat list of
/// relative paths with forward slashes, and the bytes of one of them. An abstraction so the builder is tested on
/// an in-memory folder; the real one is <c>FolderDiagnosticsSource</c> in the App.
/// </summary>
public interface IDiagnosticsSource
{
    /// <summary>Every file under the folder, as a path relative to it using '/' separators.</summary>
    IReadOnlyList<string> ListFiles();

    /// <summary>
    /// The file's bytes, or null if it vanished or cannot be read (a log the app still holds open, say). A file longer than
    /// <paramref name="maxBytes"/> is never read whole: only its last <paramref name="maxBytes"/> bytes come back, marked truncated.
    /// </summary>
    DiagnosticsFile? TryRead(string relativePath, long maxBytes);
}

/// <summary>What <see cref="IDiagnosticsSource.TryRead"/> returns.</summary>
/// <param name="Bytes">The file, or its tail.</param>
/// <param name="Truncated">True when only the tail of a longer file is here (the first line may be partial).</param>
public sealed record DiagnosticsFile(byte[] Bytes, bool Truncated);
