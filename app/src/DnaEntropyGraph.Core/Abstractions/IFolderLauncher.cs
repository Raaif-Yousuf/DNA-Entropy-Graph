namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Opens Windows Explorer on a saved file so Presentation never starts a process itself.</summary>
public interface IFolderLauncher
{
    /// <summary>Shows <paramref name="filePath"/> selected in its folder. False when Windows could not open it (never throws for that).</summary>
    bool RevealFile(string filePath);
}
