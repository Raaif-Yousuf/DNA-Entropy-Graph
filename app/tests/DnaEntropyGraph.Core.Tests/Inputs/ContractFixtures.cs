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

    // Walks up from the test binary first, then from this source file. The second start matters
    // because a build with a private --artifacts-path (what every parallel agent lane uses, and
    // what scripts/heavy.py adds) puts the binary under %TEMP%, which has no repo ancestor.
    // MEASURED 2026-10-02: with --artifacts-path under %TEMP% every ContractFixtures user threw
    // DirectoryNotFoundException.
    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string? thisFile = null)
    {
        var starts = new List<string> { AppContext.BaseDirectory };
        if (!string.IsNullOrEmpty(thisFile))
        {
            starts.Add(System.IO.Path.GetDirectoryName(thisFile)!);
        }
        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "tests", "contract-fixtures")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no ancestor of '{AppContext.BaseDirectory}' or of this source file contains tests/contract-fixtures).");
    }
}
