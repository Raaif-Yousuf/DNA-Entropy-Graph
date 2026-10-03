namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Wraps the WinRT file/folder picker so Presentation never touches WinUI types.</summary>
public interface IFilePicker
{
    /// <summary>Lets the user choose one or more sequence files. Empty when the picker was cancelled.</summary>
    Task<IReadOnlyList<string>> PickInputFilesAsync(CancellationToken cancellationToken);

    Task<string?> PickOutputFolderAsync(CancellationToken cancellationToken);

    /// <summary>Lets the user choose a program (.exe or .bat), for Browse when IGV or Geneious is not found. Null when cancelled (#586).</summary>
    Task<string?> PickProgramAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lets the user choose where to save a .zip, starting in Downloads with <paramref name="suggestedFileName"/>.
    /// Null when the picker was cancelled.
    /// </summary>
    Task<string?> PickSaveZipAsync(string suggestedFileName, CancellationToken cancellationToken);
}
