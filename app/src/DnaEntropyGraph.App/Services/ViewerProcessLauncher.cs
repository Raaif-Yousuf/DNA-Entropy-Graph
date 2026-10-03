using System.Diagnostics;
using DnaEntropyGraph.Core.Viewers;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Starts IGV or Geneious (issue #586). Never through a shell: an explicit program path, one argument per entry, so a file
/// name can only ever be a file name (a .bat or .cmd with a cmd.exe metacharacter in an argument is refused, see
/// <see cref="ViewerLaunchSafety"/>). A failure is false, never thrown, so the page can name an action.
/// </summary>
public sealed class ViewerProcessLauncher : IViewerProcessLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    public ViewerProcessLauncher()
        : this(info => Process.Start(info)?.Dispose())
    {
    }

    /// <summary>Takes the start step so a test can prove a refusal without a process ever being created.</summary>
    public ViewerProcessLauncher(Action<ProcessStartInfo> start) => _start = start;

    public bool Launch(string programPath, IReadOnlyList<string> arguments)
    {
        // The opener refuses first and says why; this is the backstop for any other caller.
        if (!File.Exists(programPath) || ViewerLaunchSafety.RefusesArguments(programPath, arguments))
        {
            return false;
        }

        var info = new ProcessStartInfo(programPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(programPath) ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            _start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}

