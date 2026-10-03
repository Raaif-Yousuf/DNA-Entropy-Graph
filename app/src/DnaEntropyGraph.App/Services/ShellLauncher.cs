using System.Diagnostics;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Opens result files and folders with Windows and copies text (issue #102). Never writes to a file (Hard Rule 14). Every
/// failure is reported as false, never thrown, so the page can name an action. The clipboard call must run on the UI thread;
/// the Results page's commands are button clicks, which do.
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    public bool OpenFile(string path)
        => File.Exists(path) && ShellOpenPolicy.MayOpen(path) && Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public bool ShowInFolder(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var explorer = new ProcessStartInfo("explorer.exe");
        explorer.ArgumentList.Add("/select,");
        explorer.ArgumentList.Add(path);
        return Start(explorer);
    }

    public bool OpenFolder(string folder)
        => Directory.Exists(folder) && Start(new ProcessStartInfo(folder) { UseShellExecute = true });

    public bool CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
