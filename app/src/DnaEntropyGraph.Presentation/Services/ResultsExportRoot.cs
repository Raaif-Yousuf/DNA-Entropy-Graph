using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The folder under which exports for a run the app did not write (an imported earlier-version run, issue #243) go, in a new folder of
/// their own: the folder chosen under "Save results to" (issue #1006), else the default output folder, as for a new run. A named type, not a
/// bare <c>Func&lt;string&gt;</c>, so DI can resolve it. The choice is read at each call, so a change on Settings needs no restart.
/// </summary>
public sealed class ResultsExportRoot
{
    private readonly Func<string> _defaultParent;
    private readonly Func<string?> _chosen;
    private readonly bool _defaultIsAppOwned;

    public ResultsExportRoot(Func<string> parent, bool appOwned = false, Func<string?>? chosenFolder = null)
    {
        _defaultParent = parent;
        _defaultIsAppOwned = appOwned;
        _chosen = chosenFolder ?? (() => null);
    }

    /// <summary>
    /// True when the folder is the app's own (portable <c>outputs</c>), so it may be made on first use; false for the user's own (Downloads, or a folder they
    /// chose), which must already be there. A chosen folder is always the user's own.
    /// </summary>
    public bool AppOwned => Resolve().AppOwned;

    public string Parent() => Resolve().Parent;

    /// <summary>
    /// The parent and its ownership from ONE read of the setting. A save uses this once, so a change on Settings in the middle of it cannot pair one
    /// folder's target with another's "must already exist" root.
    /// </summary>
    public (string Parent, bool AppOwned) Resolve()
    {
        var chosen = _chosen();
        return chosen is null ? (_defaultParent(), _defaultIsAppOwned) : (chosen, false);
    }

    /// <summary>Reads the chosen folder from the saved setting at each call (drive roots and an unreadable setting mean the default).</summary>
    public static ResultsExportRoot FromSettings(ISettingsStore store, Func<string> defaultParent, bool appOwned = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new ResultsExportRoot(defaultParent, appOwned, () => OutputFolderSetting.ReadUsable(store));
    }
}
