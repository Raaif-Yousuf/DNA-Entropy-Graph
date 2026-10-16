using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public class TestPathsTests
{
    [Fact]
    public void The_repo_root_contains_the_solution_whatever_the_artifacts_path()
    {
        File.Exists(Path.Combine(TestPaths.RepoRoot, "app", "DnaEntropyGraph.sln")).ShouldBeTrue(TestPaths.RepoRoot);
        File.Exists(TestPaths.ReswPath).ShouldBeTrue(TestPaths.ReswPath);
    }
}
