namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The folder under which exports for a run the app did not write (an imported earlier-version run, issue #243) go, in a new folder of
/// their own: the default output folder, as for a new run. A named type, not a bare <c>Func&lt;string&gt;</c>, so DI can resolve it.
/// </summary>
public sealed class ResultsExportRoot(Func<string> parent, bool appOwned = false)
{
    /// <summary>True when the folder is the app's own (portable <c>outputs</c>), so it may be made on first use; false for the user's own (Downloads), which must already be there.</summary>
    public bool AppOwned { get; } = appOwned;

    public string Parent() => parent();
}
