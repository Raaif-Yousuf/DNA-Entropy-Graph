using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

public class CsprojDependencyGuardTests
{
    [Fact]
    public void The_real_app_tree_has_no_Google_reference_outside_Cloud_and_no_inference_package()
    {
        var csprojFiles = RepoPaths.AllCsprojFiles;

        // Vacuity check (wired-to-nothing: a guard that passes because the
        // directory it scans is empty is not a pass).
        csprojFiles.Length.ShouldBeGreaterThanOrEqualTo(13, "issue #61 built 13 .csproj files; a guard that finds fewer is not scanning the real tree.");

        var violations = CsprojDependencyScanner.Scan(csprojFiles);

        violations.ShouldBeEmpty(string.Join("\n", violations.Select(v => $"{v.ProjectPath}: {v.PackageId} - {v.Reason}")));
    }

    [Fact]
    public void A_Google_reference_outside_Cloud_is_flagged()
    {
        var tempDir = Directory.CreateTempSubdirectory("deg-guard-test-");
        try
        {
            // Deliberately NOT inside a "DnaEntropyGraph.Cloud" directory.
            var badProject = Path.Combine(tempDir.FullName, "DnaEntropyGraph.NotCloud.csproj");
            File.WriteAllText(badProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Google.Cloud.Storage.V1" />
                  </ItemGroup>
                </Project>
                """);

            var violations = CsprojDependencyScanner.Scan([badProject]);

            violations.ShouldHaveSingleItem();
            violations[0].PackageId.ShouldBe("Google.Cloud.Storage.V1");
            violations[0].Reason.ShouldContain("Hard Rule 7");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_same_Google_reference_inside_Cloud_is_not_flagged()
    {
        var tempDir = Directory.CreateTempSubdirectory("deg-guard-test-");
        try
        {
            var cloudDir = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "DnaEntropyGraph.Cloud"));
            var okProject = Path.Combine(cloudDir.FullName, "DnaEntropyGraph.Cloud.csproj");
            File.WriteAllText(okProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Google.Cloud.Storage.V1" />
                  </ItemGroup>
                </Project>
                """);

            var violations = CsprojDependencyScanner.Scan([okProject]);

            violations.ShouldBeEmpty();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("TorchSharp")]
    [InlineData("Microsoft.ML.OnnxRuntime")]
    public void An_inference_package_is_flagged_even_inside_Cloud(string packageId)
    {
        var tempDir = Directory.CreateTempSubdirectory("deg-guard-test-");
        try
        {
            var cloudDir = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "DnaEntropyGraph.Cloud"));
            var project = Path.Combine(cloudDir.FullName, "DnaEntropyGraph.Cloud.csproj");
            File.WriteAllText(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="{packageId}" />
                  </ItemGroup>
                </Project>
                """);

            var violations = CsprojDependencyScanner.Scan([project]);

            violations.ShouldHaveSingleItem();
            violations[0].Reason.ShouldContain("Hard Rule 6");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
