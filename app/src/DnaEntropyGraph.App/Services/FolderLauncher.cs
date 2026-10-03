using System.Diagnostics;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.Services;

/// <summary>Opens Explorer with a saved file selected.</summary>
public sealed class FolderLauncher : IFolderLauncher
{
    public void RevealFile(string filePath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
}
