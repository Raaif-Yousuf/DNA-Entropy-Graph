using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>#495: the repo root is found whatever <c>--artifacts-path</c> the lane builds with.</summary>
public class ContractFixturesRootTests
{
    [Fact]
    public void The_repo_root_contains_the_solution_whatever_the_artifacts_path()
    {
        File.Exists(System.IO.Path.Combine(ContractFixtures.RepoRoot, "app", "DnaEntropyGraph.sln")).ShouldBeTrue(ContractFixtures.RepoRoot);
    }
}
