using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

public sealed class LocalRunFilesTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-runs-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _appData;
    private readonly IRunInputStore _inputs = Substitute.For<IRunInputStore>();

    public LocalRunFilesTests()
    {
        _root = Path.Combine(_base, "Downloads");
        _appData = Path.Combine(_base, "AppData");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_appData);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LocalRunFiles Make() => new(_inputs, () => _root, [_appData]);

    private RunRecord Run(string? outputDir, string? outputFolderOption = null)
        => new("job-1", JobPhase.Completed, DateTimeOffset.UtcNow, OutputDir: outputDir,
            OptionsJson: RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", OutputFolder = outputFolderOption }));

    private string MakeRunFolder(string name = "sample")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.bedgraph"), "x");
        return folder;
    }

    [Fact]
    public void Deleting_the_runs_own_folder_removes_it_and_reports_deleted()
    {
        var folder = MakeRunFolder();

        Make().DeleteOutputFolder(Run(folder)).ShouldBe(LocalDeleteStatus.Deleted);

        Directory.Exists(folder).ShouldBeFalse();
        Directory.Exists(_root).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_with_a_file_open_elsewhere_is_failed_not_thrown_and_keeps_its_files()
    {
        var folder = MakeRunFolder();
        using var held = new FileStream(Path.Combine(folder, "a.bedgraph"), FileMode.Open, FileAccess.Read, FileShare.None);

        Make().DeleteOutputFolder(Run(folder)).ShouldBe(LocalDeleteStatus.Failed);

        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public void Deleting_a_folder_that_is_already_gone_is_nothing_to_delete()
        => Make().DeleteOutputFolder(Run(Path.Combine(_root, "gone"))).ShouldBe(LocalDeleteStatus.NothingToDelete);

    [Fact]
    public void A_run_without_a_recorded_folder_has_nothing_to_delete()
        => Make().DeleteOutputFolder(Run(null)).ShouldBe(LocalDeleteStatus.NothingToDelete);

    [Fact]
    public void A_folder_outside_the_output_root_is_refused_and_survives()
    {
        var outside = Path.Combine(_base, "Documents");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "thesis.docx"), "keep");

        Make().DeleteOutputFolder(Run(outside)).ShouldBe(LocalDeleteStatus.Refused);

        File.Exists(Path.Combine(outside, "thesis.docx")).ShouldBeTrue();
    }

    [Fact]
    public void A_path_traversal_out_of_the_output_root_is_refused()
    {
        var outside = Path.Combine(_base, "Documents");
        Directory.CreateDirectory(outside);

        var sneaky = Path.Combine(_root, "..", "Documents");

        Make().DeleteOutputFolder(Run(sneaky)).ShouldBe(LocalDeleteStatus.Refused);
        Directory.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public void The_output_root_itself_is_refused()
    {
        MakeRunFolder();

        Make().DeleteOutputFolder(Run(_root)).ShouldBe(LocalDeleteStatus.Refused);

        Directory.Exists(_root).ShouldBeTrue();
    }

    [Fact]
    public void A_sibling_whose_name_merely_starts_with_the_root_name_is_refused()
    {
        var lookalike = _root + "-backup";
        Directory.CreateDirectory(lookalike);

        Make().DeleteOutputFolder(Run(lookalike)).ShouldBe(LocalDeleteStatus.Refused);

        Directory.Exists(lookalike).ShouldBeTrue();
    }

    [Fact]
    public void The_app_data_input_copy_is_never_deleted_even_when_the_output_root_covers_it()
    {
        // The user chose the app data folder as the output folder: the run's folder is the input copy's parent.
        var inputCopy = Path.Combine(_appData, "runs", "job-1", "input");
        Directory.CreateDirectory(inputCopy);
        File.WriteAllText(Path.Combine(inputCopy, "in.gb"), "keep");
        var runFolder = Path.Combine(_appData, "runs");

        Make().DeleteOutputFolder(Run(runFolder, outputFolderOption: _appData)).ShouldBe(LocalDeleteStatus.Refused);

        File.Exists(Path.Combine(inputCopy, "in.gb")).ShouldBeTrue();
    }

    [Fact]
    public void The_run_folder_is_resolved_under_the_option_folder_when_the_run_chose_one()
    {
        var custom = Path.Combine(_base, "Lab");
        var folder = Path.Combine(custom, "sample");
        Directory.CreateDirectory(folder);

        Make().DeleteOutputFolder(Run(folder, outputFolderOption: custom)).ShouldBe(LocalDeleteStatus.Deleted);
    }

    [Fact]
    public void Output_folder_exists_reflects_the_disk()
    {
        var folder = MakeRunFolder();
        var files = Make();

        files.OutputFolderExists(Run(folder)).ShouldBeTrue();
        files.OutputFolderExists(Run(Path.Combine(_root, "gone"))).ShouldBeFalse();
        files.OutputFolderExists(Run(null)).ShouldBeFalse();
    }

    [Fact]
    public void Rerun_prefers_the_app_copy_of_the_input_then_the_original_then_nothing()
    {
        var original = Path.Combine(_base, "original.gb");
        File.WriteAllText(original, "x");
        var staged = Path.Combine(_base, "staged.gb");
        File.WriteAllText(staged, "x");
        var options = new RunOptions { ModelId = "m", RunTarget = "cloud", InputPath = original };
        var files = Make();

        _inputs.TryFindStagedInput("job-1").Returns(staged);
        files.FindRerunInput(Run(null), options).ShouldBe(staged);

        _inputs.TryFindStagedInput("job-1").Returns((string?)null);
        files.FindRerunInput(Run(null), options).ShouldBe(original);

        File.Delete(original);
        files.FindRerunInput(Run(null), options).ShouldBeNull();
    }
}

public sealed class RunOptionsJsonTests
{
    [Fact]
    public void Options_round_trip_with_enums_as_text()
    {
        var options = new RunOptions { ModelId = "evo2_7b", RunTarget = "cloud", SpotVm = true, Direction = Direction.ForwardOnly, GpuTier = GpuTier.A100_40, Seed = 7, InputPath = "C:\\in.gb" };

        var json = RunOptionsJson.Serialize(options);

        json.ShouldContain("\"ForwardOnly\"");
        RunOptionsJson.TryDeserialize(json).ShouldBe(options);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    public void Unreadable_or_incomplete_options_are_null_not_an_exception(string json)
        => RunOptionsJson.TryDeserialize(json).ShouldBeNull();
}
