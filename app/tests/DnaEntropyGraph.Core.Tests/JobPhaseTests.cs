using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Inputs;
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

    /// <summary>
    /// Issue #65's own "Observable that proves it is wired": every option named in spec
    /// section 4.3's table (cross-checked against what the worker's manifest can actually
    /// honour) has the default the table specifies. This is the decisive test for #65 - a
    /// default silently dropped or renamed here fails this test, not just "compiles".
    /// </summary>
    [Fact]
    public void Every_spec_4_3_option_has_its_documented_default()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" };

        // Input
        options.Format.ShouldBe(InputFormat.Auto);
        options.TreatAsRna.ShouldBeFalse();
        options.StartCoordinate.ShouldBe(1);
        options.FastaRecords.ShouldBe(FastaRecordsSelection.All);
        options.AmbiguityPolicy.ShouldBe(AmbiguityPolicy.Keep);

        // Model and GPU
        options.GpuTier.ShouldBe(GpuTier.CheapestAvailable);
        options.Predictor.ShouldBe(PredictorSelection.Evo2);
        options.Seed.ShouldBe(0);
        options.ContextLength.ShouldBe(4096);
        options.Direction.ShouldBe(Direction.BothCombined);
        options.Window.ShouldBe(8192);
        options.Stride.ShouldBe(4096);

        // Genes
        options.FindGenes.ShouldBeTrue();

        // Output
        options.OutputFiles.ShouldBe(OutputFileKinds.All);
        options.TrackFormat.ShouldBe(TrackFormat.BedGraph);
        options.NameTemplate.ShouldBe("{file}");

        // Cloud
        options.ZonePreference.ShouldBeNull();
        options.SpotVm.ShouldBeFalse();
        options.AfterTask.ShouldBe(AfterTaskAction.Stop);
        options.KeepAliveMinutes.ShouldBe(30);
        options.AfterKeepAlive.ShouldBe(AfterKeepAliveAction.Stop);
        options.MaxRunDurationMinutes.ShouldBe(240);
        options.CloudResultsRetentionDays.ShouldBe(90);
        options.BootDiskGb.ShouldBe(150);

        // Local / notifications
        options.OpenViewerWhenDone.ShouldBeTrue();
        options.ToastWhenDone.ShouldBeTrue();
        options.OpenFolderWhenDone.ShouldBeFalse();
        options.PlaySoundWhenDone.ShouldBeFalse();
        options.KeepWorkerLogInOutputFolder.ShouldBeFalse();
    }

    [Fact]
    public void Existing_construction_sites_that_set_only_the_three_original_properties_still_compile_and_get_every_new_default()
    {
        // The exact shape Lane C's/Cloud's/Presentation's existing call sites use today -
        // this is the compile-time proof that adding options never forced a `required`
        // member or broke a caller that only knows about the original three properties.
        RunOptions BuildLikeAnExistingCaller() => new() { ModelId = "evo2_7b", RunTarget = "Cloud", OutputFolder = null };

        var options = BuildLikeAnExistingCaller();
        options.OutputFiles.ShouldBe(OutputFileKinds.All);
    }
}
