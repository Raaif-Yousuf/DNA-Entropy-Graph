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

    [Fact]
    public void A_row_stored_with_the_removed_when_done_switches_still_reads_and_the_options_no_longer_carry_them()
    {
        // #643 cold review (5): the run options once carried four "when done" switches that nothing read (the live switches are
        // Settings' NotificationSettings). Rows written then still hold the keys; they must read back, and a re-run must not
        // grow a second copy of the setting.
        var stored = RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" })
            .TrimEnd('}') + ",\"OpenViewerWhenDone\":true,\"ToastWhenDone\":true,\"OpenFolderWhenDone\":false,\"PlaySoundWhenDone\":false}";

        var back = RunOptionsJson.TryDeserialize(stored);

        back.ShouldNotBeNull();
        back.ModelId.ShouldBe("evo2_7b");
        typeof(RunOptions).GetProperties().Select(p => p.Name).Where(n => n.EndsWith("WhenDone", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static string StoredWith(string outputFiles)
        => System.Text.RegularExpressions.Regex.Replace(
            RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }),
            "\"OutputFiles\":\"[^\"]*\"",
            "\"OutputFiles\":\"" + outputFiles + "\"");

    [Fact]
    public void A_row_stored_before_the_newer_files_existed_still_means_the_files_it_was_written_with()
    {
        // MEASURED 2026-10-03: JsonStringEnumConverter writes a flags value that equals a named member by that name, so every
        // run stored before #103 says "All". Its meaning is frozen at the original eight files.
        RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", OutputFiles = OutputFileKinds.All })
            .ShouldContain("\"OutputFiles\":\"All\"");

        var back = RunOptionsJson.TryDeserialize(StoredWith("All"))!;

        back.OutputFiles.ShouldBe(OutputFileKinds.GenBank | OutputFileKinds.Fasta | OutputFileKinds.BedGraph | OutputFileKinds.Wig
            | OutputFileKinds.GeneiousGff3 | OutputFileKinds.GenesGff3 | OutputFileKinds.Stats | OutputFileKinds.EntropyTsv);
        back.OutputFiles.HasFlag(OutputFileKinds.Smoothed).ShouldBeFalse();
    }

    [Fact]
    public void A_stored_comma_list_of_the_original_flags_reads_back_unchanged()
        => RunOptionsJson.TryDeserialize(StoredWith("GenBank, Wig, Stats"))!.OutputFiles
            .ShouldBe(OutputFileKinds.GenBank | OutputFileKinds.Wig | OutputFileKinds.Stats);

    [Fact]
    public void A_selection_with_the_new_files_round_trips()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", OutputFiles = OutputCatalog.Defaults | OutputFileKinds.Probs };

        RunOptionsJson.TryDeserialize(RunOptionsJson.Serialize(options))!.OutputFiles.ShouldBe(options.OutputFiles);
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
