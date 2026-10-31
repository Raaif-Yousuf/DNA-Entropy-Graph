using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #830: a run, a keep-alive row and a Cloud page listing act in the project and Google account they were made in, never in whatever is selected later. The
/// SELECTION (<c>SelectedProjectId</c>, <c>CurrentAccount</c>, <c>SelectedScope()</c>) is therefore read only where the selection IS the question: the wizard and setup, the shell
/// pill, the budget and billing path, the Cloud page's listing, the import of an existing run, the new-run capture in <c>JobEngine</c>, the left-running scan's own selection, and the
/// one place the app hands the selected scope to the reconciler's sweeps. Every other class takes a <c>CloudScope</c> as an argument or reads it from a run row (<c>RunScope.Of</c>).
/// A new read elsewhere fails here; the fix is to pass the scope in, or (when it really is a selection path) to add the file below WITH its reason. An entry that no longer matches
/// anything fails too, so the list cannot rot (Rule 18: an allowlist that expires itself).
/// </summary>
public class SelectionReadGuardTests
{
    private static readonly Regex Read = new(@"\bSelectedProjectId\b|\bCurrentAccount\b|\bSelectedScope\(", RegexOptions.Compiled);

    /// <summary>File name to why the selection is read there. A file is matched by its name, so two files of one name would share an entry (none do).</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["IGcpAccount.cs"] = "the definition of the selection",
        ["GoogleAccountService.cs"] = "the real implementation of the selection",
        ["FakeGcp.cs"] = "the fake's implementation of the selection",
        ["FakeGcp.Scope.cs"] = "the fake's implementation of the selection",
        ["WizardViewModel.cs"] = "the setup wizard chooses the account and project",
        ["SetupStepsViewModel.cs"] = "the setup steps act on the project being set up",
        ["SetupCheckService.cs"] = "the setup checks act on the project being set up, as the current account",
        ["ShellViewModel.cs"] = "the status pill names the current account",
        ["BudgetSettingsViewModel.cs"] = "budget and billing settings are about the selected project",
        ["QuotaRequest.cs"] = "a quota increase is asked for the selected project, as the current account",
        ["JobEngine.cs"] = "the ONE capture of the selected scope when a new run is created; the run keeps it from then on",
        ["CloudResourcesViewModel.cs"] = "the Cloud page lists the selected project; each row keeps the scope it was listed in",
        ["HistoryViewModel.cs"] = "importing an existing run adopts the selected project and account",
        ["LeftRunningGuard.cs"] = "the left-running scan adds the selected scope to the scopes of recent runs",
        ["ServiceRegistration.cs"] = "hands the selected project to the budget gateway and the selected scope to the reconciler's sweeps",
    };

    private static IEnumerable<(string Path, int Count)> Reads(IEnumerable<(string Path, string Text)> files)
        => files
            .Select(f => (f.Path, Count: f.Text.Split('\n').Count(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && Read.IsMatch(line))))
            .Where(f => f.Count > 0);

    private static IEnumerable<string> Violations(IEnumerable<(string Path, string Text)> files)
        => Reads(files).Where(r => !Allowed.ContainsKey(Path.GetFileName(r.Path))).Select(r => r.Path);

    [Fact]
    public void The_selection_is_read_only_in_the_named_selection_paths()
    {
        var files = RepoPaths.AllCSharpFiles.Select(p => (p, File.ReadAllText(p))).ToList();

        Violations(files).ShouldBeEmpty("take a CloudScope as an argument (or RunScope.Of(row)); read the selection only in an allowlisted selection path");
    }

    [Fact]
    public void Every_allowlist_entry_still_matches_a_read_and_the_scan_sees_the_definition()
    {
        var found = Reads(RepoPaths.AllCSharpFiles.Select(p => (p, File.ReadAllText(p)))).Select(r => Path.GetFileName(r.Path)).ToHashSet(StringComparer.Ordinal);

        found.ShouldContain("IGcpAccount.cs", "vacuity: the scan must see the definition, or it reads nothing");
        Allowed.Keys.Where(k => !found.Contains(k)).ShouldBeEmpty("a stale entry: delete it from the allowlist (and say so in the commit)");
    }

    [Fact]
    public void The_scan_catches_a_planted_read_and_ignores_a_comment()
    {
        var planted = new[]
        {
            ("src/Elsewhere/SomeRunner.cs", "var project = _account.SelectedProjectId;"),
            ("src/Elsewhere/Another.cs", "var who = _account.CurrentAccount?.Sub;"),
            ("src/Elsewhere/Third.cs", "var scope = _account.SelectedScope();"),
            ("src/Elsewhere/Commented.cs", "    // the selected project (SelectedProjectId) is never read here"),
            ("src/Elsewhere/Fine.cs", "var scope = RunScope.Of(row);"),
        };

        Violations(planted).ShouldBe(["src/Elsewhere/SomeRunner.cs", "src/Elsewhere/Another.cs", "src/Elsewhere/Third.cs"], ignoreOrder: true);
    }
}
