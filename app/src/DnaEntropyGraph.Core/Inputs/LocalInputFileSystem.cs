using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary><see cref="IInputFileSystem"/> on the local disk.</summary>
public sealed class LocalInputFileSystem : IInputFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> SequenceFiles(string folder) => InputFolderScanner.SequenceFiles(folder);

    public InputResolution Resolve(string? text) => InputResolver.Resolve(text);
}
