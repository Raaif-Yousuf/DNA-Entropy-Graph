using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// #874: a worktree lane runs the primary's scripts/heavy.py, so the test binary can sit under another checkout's
/// app/.artifacts. The guards must still scan the tree that was compiled, never the one the binary happens to live in.
/// </summary>
public class RepoPathsTests
{
    [Fact]
    public void AppRoot_is_the_app_directory_that_contains_the_compiled_RepoPaths_source()
    {
        var sourceAppDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

        File.Exists(Path.Combine(sourceAppDir, "DnaEntropyGraph.sln")).ShouldBeTrue("vacuity: the source-derived app dir must hold the sln");
        RepoPaths.AppRoot.TrimEnd(Path.DirectorySeparatorChar).ShouldBe(sourceAppDir.TrimEnd(Path.DirectorySeparatorChar), StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void FindAppRoot_prefers_the_source_tree_over_the_binary_location()
    {
        // The primary's checkout stands in for "the binary lives under another tree": a CallerFilePath
        // in a fake worktree must win over BaseDirectory, which here sits under the real app/.
        var fake = Path.Combine(Path.GetTempPath(), "deg-repopaths-" + Guid.NewGuid().ToString("N"));
        var fakeTests = Path.Combine(fake, "app", "tests", "X");
        Directory.CreateDirectory(fakeTests);
        File.WriteAllText(Path.Combine(fake, "app", "DnaEntropyGraph.sln"), string.Empty);
        try
        {
            RepoPaths.FindAppRoot(Path.Combine(fakeTests, "RepoPaths.cs"))
                .TrimEnd(Path.DirectorySeparatorChar).ShouldBe(Path.Combine(fake, "app"), StringCompareShould.IgnoreCase);
        }
        finally
        {
            Directory.Delete(fake, true);
        }
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
