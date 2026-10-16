using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// #495. The repo-root lookup must work when the test binary is outside the tree (a lane builds with
/// <c>--artifacts-path $env:TEMP\deg-art\&lt;lane&gt;</c>), so it falls back to this source file's own path.
/// </summary>
public class FixturePathsTests
{
    [Fact]
    public void The_repo_root_contains_the_solution_whatever_the_artifacts_path()
    {
        File.Exists(Path.Combine(FixturePaths.RepoRoot, "app", "DnaEntropyGraph.sln")).ShouldBeTrue(FixturePaths.RepoRoot);
    }

    [Fact]
    public void The_cloud_error_classification_fixture_is_found_from_the_root()
    {
        File.Exists(FixturePaths.CloudErrorClassificationJson).ShouldBeTrue(FixturePaths.CloudErrorClassificationJson);
    }
}
