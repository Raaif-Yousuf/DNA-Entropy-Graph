namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Opens Windows Explorer on a saved file so Presentation never starts a process itself.</summary>
public interface IFolderLauncher
{
    /// <summary>Shows <paramref name="filePath"/> selected in its folder.</summary>
    void RevealFile(string filePath);
}
