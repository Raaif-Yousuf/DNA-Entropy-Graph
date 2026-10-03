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
            UnlinkJunctions(new DirectoryInfo(_base));
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // A recursive delete cannot cross a junction, so a test that left one behind would leak its folder: remove the links first.
    private static void UnlinkJunctions(DirectoryInfo folder)
    {
        foreach (var directory in folder.EnumerateDirectories())
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                directory.Delete(recursive: false);
            }
            else
            {
                UnlinkJunctions(directory);
            }
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

        Make().DeleteOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.Deleted);

        Directory.Exists(folder).ShouldBeFalse();
        Directory.Exists(_root).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_whose_only_file_is_open_elsewhere_is_in_use_not_thrown_and_keeps_its_files()
    {
        var folder = MakeRunFolder();
        using var held = new FileStream(Path.Combine(folder, "a.bedgraph"), FileMode.Open, FileAccess.Read, FileShare.None);

        Make().DeleteOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.InUse);

        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_with_one_file_open_elsewhere_is_partial_and_counts_what_went_and_what_stayed()
    {
        var folder = MakeRunFolder();
        File.WriteAllText(Path.Combine(folder, "b.gff3"), "y");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "c.txt"), "z");
        using var held = new FileStream(Path.Combine(folder, "a.bedgraph"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = Make().DeleteOutputFolder(Run(folder));

        result.ShouldBe(new LocalDeleteResult(LocalDeleteStatus.Partial, FilesDeleted: 2, FilesRemaining: 1));
        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "b.gff3")).ShouldBeFalse();
        Directory.Exists(Path.Combine(folder, "sub")).ShouldBeFalse();
    }

    [Fact]
    public void A_read_only_file_is_access_denied_not_in_use_and_stays()
    {
        var folder = MakeRunFolder();
        var file = Path.Combine(folder, "a.bedgraph");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        try
        {
            Make().DeleteOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.AccessDenied);
            File.Exists(file).ShouldBeTrue();
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_folder_whose_files_all_delete_reports_the_count()
    {
        var folder = MakeRunFolder();
        File.WriteAllText(Path.Combine(folder, "b.gff3"), "y");

        Make().DeleteOutputFolder(Run(folder)).ShouldBe(new LocalDeleteResult(LocalDeleteStatus.Deleted, FilesDeleted: 2, FilesRemaining: 0));
    }

    private static void MakeJunction(string link, string target)
    {
        using var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        mk!.WaitForExit();
        mk.ExitCode.ShouldBe(0, "could not create the test junction");
    }

    private string MakePreciousFolder()
    {
        var target = Path.Combine(_base, "Precious");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "keep me");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        File.WriteAllText(Path.Combine(target, "sub", "deep.txt"), "keep me too");
        return target;
    }

    [Fact]
    public void A_run_folder_that_is_itself_a_junction_is_refused_and_the_target_survives()
    {
        var target = MakePreciousFolder();
        var link = Path.Combine(_root, "sample");
        MakeJunction(link, target);

        var result = Make().DeleteOutputFolder(Run(link));

        result.Status.ShouldBe(LocalDeleteStatus.LinkRefused);
        File.ReadAllText(Path.Combine(target, "precious.txt")).ShouldBe("keep me");
        File.ReadAllText(Path.Combine(target, "sub", "deep.txt")).ShouldBe("keep me too");
        Directory.Exists(link).ShouldBeTrue();
    }

    [Fact]
    public void A_run_folder_under_a_junctioned_parent_is_refused_and_the_target_survives()
    {
        var target = MakePreciousFolder();
        var runFolderInTarget = Path.Combine(target, "run");
        Directory.CreateDirectory(runFolderInTarget);
        File.WriteAllText(Path.Combine(runFolderInTarget, "a.bedgraph"), "x");
        var linkedParent = Path.Combine(_root, "group");
        MakeJunction(linkedParent, target);

        var result = Make().DeleteOutputFolder(Run(Path.Combine(linkedParent, "run")));

        result.Status.ShouldBe(LocalDeleteStatus.LinkRefused);
        File.ReadAllText(Path.Combine(runFolderInTarget, "a.bedgraph")).ShouldBe("x");
        File.ReadAllText(Path.Combine(target, "precious.txt")).ShouldBe("keep me");
    }

    [Fact]
    public void A_junction_inside_the_run_folder_is_removed_as_a_link_and_its_target_is_untouched()
    {
        var folder = MakeRunFolder();
        var target = Path.Combine(_base, "Elsewhere");
        Directory.CreateDirectory(target);
        var precious = Path.Combine(target, "precious.txt");
        File.WriteAllText(precious, "keep me");
        MakeJunction(Path.Combine(folder, "link"), target);

        var result = Make().DeleteOutputFolder(Run(folder));

        File.ReadAllText(precious).ShouldBe("keep me");
        Directory.Exists(target).ShouldBeTrue();
        result.Status.ShouldBe(LocalDeleteStatus.Deleted);
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void Deleting_a_folder_that_is_already_gone_is_nothing_to_delete()
        => Make().DeleteOutputFolder(Run(Path.Combine(_root, "gone"))).Status.ShouldBe(LocalDeleteStatus.NothingToDelete);

    [Fact]
    public void A_run_without_a_recorded_folder_has_nothing_to_delete()
        => Make().DeleteOutputFolder(Run(null)).Status.ShouldBe(LocalDeleteStatus.NothingToDelete);

    [Fact]
    public void A_folder_outside_the_output_root_is_refused_and_survives()
    {
        var outside = Path.Combine(_base, "Documents");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "thesis.docx"), "keep");

        Make().DeleteOutputFolder(Run(outside)).Status.ShouldBe(LocalDeleteStatus.Refused);

        File.Exists(Path.Combine(outside, "thesis.docx")).ShouldBeTrue();
    }

    [Fact]
    public void A_path_traversal_out_of_the_output_root_is_refused()
    {
        var outside = Path.Combine(_base, "Documents");
        Directory.CreateDirectory(outside);

        var sneaky = Path.Combine(_root, "..", "Documents");

        Make().DeleteOutputFolder(Run(sneaky)).Status.ShouldBe(LocalDeleteStatus.Refused);
        Directory.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public void The_output_root_itself_is_refused()
    {
        MakeRunFolder();

        Make().DeleteOutputFolder(Run(_root)).Status.ShouldBe(LocalDeleteStatus.Refused);

        Directory.Exists(_root).ShouldBeTrue();
    }

    [Fact]
    public void A_sibling_whose_name_merely_starts_with_the_root_name_is_refused()
    {
        var lookalike = _root + "-backup";
        Directory.CreateDirectory(lookalike);

        Make().DeleteOutputFolder(Run(lookalike)).Status.ShouldBe(LocalDeleteStatus.Refused);

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

        Make().DeleteOutputFolder(Run(runFolder, outputFolderOption: _appData)).Status.ShouldBe(LocalDeleteStatus.Refused);

        File.Exists(Path.Combine(inputCopy, "in.gb")).ShouldBeTrue();
    }

    [Fact]
    public void The_run_folder_is_resolved_under_the_option_folder_when_the_run_chose_one()
    {
        var custom = Path.Combine(_base, "Lab");
        var folder = Path.Combine(custom, "sample");
        Directory.CreateDirectory(folder);

        Make().DeleteOutputFolder(Run(folder, outputFolderOption: custom)).Status.ShouldBe(LocalDeleteStatus.Deleted);
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
