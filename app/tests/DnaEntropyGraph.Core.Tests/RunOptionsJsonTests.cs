using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #59: the run row's <c>OptionsJson</c> is written by the engine and read back by the reconciler; one serializer owns both directions.</summary>
public sealed class RunOptionsJsonTests
{
    [Fact]
    public void A_serialized_option_set_reads_back_with_its_enums_and_paths()
    {
        var options = new RunOptions
        {
            ModelId = "evo2_7b",
            RunTarget = "Cloud",
            InputPath = @"C:\data\SetTnpB.gb",
            OutputFolder = @"C:\out",
            AfterTask = AfterTaskAction.Delete,
            GpuTier = GpuTier.A100_40,
        };

        var json = RunOptionsJson.Serialize(options);
        var back = RunOptionsJson.TryDeserialize(json);

        json.ShouldContain("\"Delete\"", Case.Sensitive, "enums are written by name so a reordered enum cannot change a stored row");
        back.ShouldBe(options);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("null")]
    public void Anything_that_is_not_a_complete_option_set_reads_as_null(string json)
    {
        RunOptionsJson.TryDeserialize(json).ShouldBeNull();
    }
}
