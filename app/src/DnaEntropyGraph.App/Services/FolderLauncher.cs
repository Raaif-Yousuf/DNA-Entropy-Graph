using System.ComponentModel;
using System.Diagnostics;
using DnaEntropyGraph.Core;
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
            // ArgumentList, not a hand-quoted string: a folder ending in a backslash (D:\) would otherwise end in an escaped quote.
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(folderPath);
            Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public bool RevealFile(string filePath)
    {
        // Explorer reads this switch with its own rules, so the quoting is DnaEntropyGraph.Core.ExplorerCommandLine's (tested); a path it refuses is "could not reveal".
        if (ExplorerCommandLine.Select(filePath) is not { } arguments)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
