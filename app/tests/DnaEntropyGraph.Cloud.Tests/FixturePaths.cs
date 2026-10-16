namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Locates the repo-root <c>tests/contract-fixtures/</c> directory at
/// test-run time by walking up from the test assembly's own output
/// directory until <c>app/DnaEntropyGraph.sln</c> is found (same technique
/// as <c>DnaEntropyGraph.Guards.Tests/RepoPaths.cs</c>). The fixtures
/// there are read-only golden vectors extracted from the prototype -
/// consumed, never edited, never copied into this test project.
/// </summary>
internal static class FixturePaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string CloudErrorClassificationJson => Path.Combine(RepoRoot, "tests", "contract-fixtures", "cloud_error_classification.json");

    // Walks up from the test binary first, then from this source file: a build with a private --artifacts-path (every
    // parallel lane) puts the binary under %TEMP%, which has no repo ancestor (#495, MEASURED 2026-10-02).
    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
        {
            var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "app", "DnaEntropyGraph.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException(
            $"Could not find app/DnaEntropyGraph.sln by walking up from {AppContext.BaseDirectory} or {thisFile}. " +
            "A fixture-driven test that cannot find its fixture would silently run zero cases and pass for the wrong reason.");
    }
}
