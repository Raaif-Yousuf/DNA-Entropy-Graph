namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Locates <c>tests/contract-fixtures/</c> (repo root, NOT under <c>app/</c>) regardless of
/// the test runner's working directory or build output depth, by walking up from
/// <see cref="AppContext.BaseDirectory" /> until that directory is found. The fixtures
/// there are read-only golden vectors produced by running the real worker (see the file
/// this walks to for provenance) - never edited by a C# test.
/// </summary>
internal static class ContractFixtures
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Path(params string[] segments)
    {
        var parts = new List<string> { RepoRoot, "tests", "contract-fixtures" };
        parts.AddRange(segments);
        return System.IO.Path.Combine([.. parts]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "tests", "contract-fixtures")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no ancestor of '{AppContext.BaseDirectory}' contains tests/contract-fixtures).");
    }
}
