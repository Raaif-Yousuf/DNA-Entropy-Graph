using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #830: a run acts in the project and Google account it was created in, never in whatever is selected later. <c>CloudJobRequest.ResolvedScope</c> therefore throws
/// when a request carries no scope, and the one production place that builds a request is <c>CloudJobRequestFactory.Create</c>, which requires one. This guard keeps a
/// second construction (which could leave the scope out and fail at run time, or be given a guessed one) from appearing in production code.
/// </summary>
public class CloudJobRequestScopeGuardTests
{
    private static readonly Regex Construction = new(@"new\s+CloudJobRequest\s*\(", RegexOptions.Compiled);

    private static IEnumerable<string> ConstructionsOutsideTheFactory(IEnumerable<(string Path, string Text)> files)
        => files.Where(f => !f.Path.EndsWith("CloudJobRequestFactory.cs", StringComparison.Ordinal) && Construction.IsMatch(f.Text)).Select(f => f.Path);

    [Fact]
    public void Only_the_factory_constructs_a_CloudJobRequest_in_production_code()
    {
        var files = RepoPaths.AllCSharpFiles.Select(p => (p, File.ReadAllText(p))).ToList();

        // Vacuity: the scan must see the factory's own construction, or it is reading nothing.
        files.Count(f => f.p.EndsWith("CloudJobRequestFactory.cs", StringComparison.Ordinal) && Construction.IsMatch(f.Item2)).ShouldBe(1);

        ConstructionsOutsideTheFactory(files).ShouldBeEmpty("build a request with CloudJobRequestFactory.Create(..., scope, ...) so it always carries one");
    }

    [Fact]
    public void The_scan_catches_a_planted_second_construction()
    {
        var planted = new[] { ("src/Elsewhere.cs", "var r = new CloudJobRequest(JobId: \"x\", Spec: s, Zones: z, Options: o, Inputs: i, OutputFolder: f);") };

        ConstructionsOutsideTheFactory(planted).ShouldBe(["src/Elsewhere.cs"]);
    }
}
