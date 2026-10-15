using System.ComponentModel;
using System.Diagnostics;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.Services;

/// <summary>Opens Explorer with a saved file selected.</summary>
public sealed class FolderLauncher : IFolderLauncher
{
    public bool OpenFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public bool RevealFile(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
