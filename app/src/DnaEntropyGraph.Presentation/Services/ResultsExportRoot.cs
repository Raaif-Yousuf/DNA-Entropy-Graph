namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The folder under which exports for a run the app did not write (an imported earlier-version run, issue #243) go, in a new folder of
/// their own: the default output folder, as for a new run. A named type, not a bare <c>Func&lt;string&gt;</c>, so DI can resolve it.
/// </summary>
public sealed class ResultsExportRoot(Func<string> parent)
{
    public string Parent() => parent();
}
