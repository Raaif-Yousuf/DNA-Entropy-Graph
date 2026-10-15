using System.Runtime.CompilerServices;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Locates the repo root for tests that read a source-controlled file. Walks up from the test binary first, then
/// from this source file: a build with a private <c>--artifacts-path</c> (every parallel lane) puts the binary under
/// <c>%TEMP%</c>, which has no repo ancestor (#495, MEASURED 2026-10-02). Throws loudly when neither finds it, so a
/// test that cannot find its file fails instead of silently reading nothing.
/// </summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ReswPath => Path.Combine(RepoRoot, "app", "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw");

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
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
            "A test that cannot find its file would silently read nothing and pass for the wrong reason.");
    }
}
