namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Wraps the WinRT file/folder picker so Presentation never touches WinUI types.</summary>
public interface IFilePicker
{
    /// <summary>Lets the user choose one or more sequence files. Empty when the picker was cancelled.</summary>
    Task<IReadOnlyList<string>> PickInputFilesAsync(CancellationToken cancellationToken);

    Task<string?> PickOutputFolderAsync(CancellationToken cancellationToken);
}
