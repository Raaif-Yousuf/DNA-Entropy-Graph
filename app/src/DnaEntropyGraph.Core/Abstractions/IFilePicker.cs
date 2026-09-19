namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Wraps the WinRT file/folder picker so Presentation never touches WinUI types.</summary>
public interface IFilePicker
{
    Task<string?> PickInputFileAsync(CancellationToken cancellationToken);

    Task<string?> PickOutputFolderAsync(CancellationToken cancellationToken);
}
