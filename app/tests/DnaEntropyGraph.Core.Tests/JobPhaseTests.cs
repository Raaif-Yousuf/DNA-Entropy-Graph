using DnaEntropyGraph.Core;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

public class JobPhaseTests
{
    [Theory]
    [InlineData(JobPhase.Draft)]
    [InlineData(JobPhase.Validating)]
    [InlineData(JobPhase.Uploading)]
    [InlineData(JobPhase.Provisioning)]
    [InlineData(JobPhase.Preparing)]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Finalizing)]
    [InlineData(JobPhase.Downloading)]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.PartiallyCompleted)]
    [InlineData(JobPhase.Cancelling)]
    [InlineData(JobPhase.Cancelled)]
    [InlineData(JobPhase.Failed)]
    public void Every_phase_from_docs_architecture_is_a_defined_member(JobPhase phase)
    {
        Enum.IsDefined(phase).ShouldBeTrue();
    }
}

public class RunOptionsTests
{
    [Fact]
    public void Stores_the_model_and_target_it_was_constructed_with()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" };

        options.ModelId.ShouldBe("evo2_7b");
        options.RunTarget.ShouldBe("Cloud");
        options.OutputFolder.ShouldBeNull();
    }
}
