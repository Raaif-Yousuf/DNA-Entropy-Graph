namespace DnaEntropyGraph.Core.Viewers;

/// <summary>Starts a desktop viewer (issue #586). Behind an interface so a ViewModel never starts a process.</summary>
public interface IViewerProcessLauncher
{
    /// <summary>
    /// Starts <paramref name="programPath"/> with these arguments, one argument each, and no shell (so a file name can never
    /// be read as a command). False when it could not be started; never throws for that.
    /// </summary>
    bool Launch(string programPath, IReadOnlyList<string> arguments);
}
