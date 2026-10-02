using System.Xml.Linq;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Hard Rule 13 for run failures: a failed run carries a code, never English
/// built in C#, and every code the app can record has a resource string that
/// names one action. A code with no string would show the user the key.
/// </summary>
public class RunErrorResourceTests
{
    private static Dictionary<string, string> ReswValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                values[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return values;
    }

    [Fact]
    public void Every_run_error_code_has_a_resource_string()
    {
        var resw = ReswValues();

        RunErrorCodes.All.Count.ShouldBeGreaterThan(10);
        foreach (var code in RunErrorCodes.All)
        {
            var key = RunErrorCodes.ResourceKey(code);
            resw.ShouldContainKey(key, $"run error code '{code}' maps to '{key}', which Resources.resw does not define");
            resw[key].Trim().ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Every_cloud_error_kind_maps_to_a_known_code()
    {
        foreach (var kind in Enum.GetValues<CloudErrorKind>())
        {
            RunErrorCodes.All.ShouldContain(RunErrorCodes.For(kind));
        }
    }

    [Fact]
    public void The_codes_map_to_distinct_keys_and_an_unknown_code_gets_the_generic_one()
    {
        RunErrorCodes.All.Select(RunErrorCodes.ResourceKey).Distinct().Count().ShouldBe(RunErrorCodes.All.Count);
        RunErrorCodes.ResourceKey("something_new").ShouldBe(RunErrorCodes.ResourceKey(RunErrorCodes.Other));
        RunErrorCodes.ResourceKey(null).ShouldBe(RunErrorCodes.ResourceKey(RunErrorCodes.Other));
    }

    [Fact]
    public void No_run_error_text_is_inline_in_the_runner_or_the_engine()
    {
        // The two files that used to carry user-reachable English. Whatever
        // is left in them is a log string or a code, not a sentence for a user.
        var root = RepoPaths.AppRoot;
        var engine = File.ReadAllText(Path.Combine(root, "src", "DnaEntropyGraph.App", "JobEngine.cs"));
        engine.ShouldNotContain("not available yet");
        engine.ShouldNotContain("No Google Cloud project");

        var pipeline = File.ReadAllText(Path.Combine(root, "src", "DnaEntropyGraph.Cloud", "CloudCallPipeline.cs"));
        pipeline.ShouldNotContain("could not be reached");
    }
}
