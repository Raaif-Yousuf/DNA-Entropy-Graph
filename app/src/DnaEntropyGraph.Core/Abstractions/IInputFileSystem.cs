using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// The disk questions the New run page asks about what a user dropped or pasted (is it a folder, which sequence files
/// are in it, does this text name a file). Each one touches the disk, so the ViewModel calls them off the UI thread (issue #63).
/// </summary>
public interface IInputFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>The sequence files directly inside <paramref name="folder"/>, in name order.</summary>
    IReadOnlyList<string> SequenceFiles(string folder);

    /// <summary>What a dropped path or pasted text means (see <see cref="InputResolver"/>).</summary>
    InputResolution Resolve(string? text);
}
