using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
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

    [Fact]
    public void A_drive_root_output_folder_never_makes_a_folder_below_it_deletable()
    {
        // Hard Rule 14: OutputFolder "C:\" with a row naming C:\...\x must stay Refused, not become eligible for a recursive delete.
        var driveRoot = Path.GetPathRoot(_base)!;
        var folder = MakeRunFolder();

        var result = Make().DeleteOutputFolder(Run(folder, outputFolderOption: driveRoot));

        // Issue #954: refused for its own reason, so the History can say why (a drive root) instead of a bare "refused".
        result.Status.ShouldBe(LocalDeleteStatus.DriveRootRefused);
        Directory.Exists(folder).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
        Make().DeleteUnmarkedOutputFolder(Run(folder, outputFolderOption: driveRoot)).Status.ShouldBe(LocalDeleteStatus.DriveRootRefused, "a confirmation never unlocks a drive root");
        Make().PlanDelete(Run(folder, outputFolderOption: driveRoot)).ShouldBe(LocalDeletePlan.DriveRoot);
        Make().OutputRootIsDriveRoot(Run(folder, outputFolderOption: driveRoot)).ShouldBeTrue();
    }

    [Fact]
    public void A_drive_root_output_folder_is_named_as_the_reason_only_when_the_folder_is_on_that_drive()
    {
        var outside = Path.Combine(Path.GetTempPath(), "deg-elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var run = Run(outside, outputFolderOption: Path.GetPathRoot(outside));

            Make().DeleteOutputFolder(run).Status.ShouldBe(LocalDeleteStatus.DriveRootRefused);
            Make().OutputRootIsDriveRoot(run).ShouldBeTrue();

            var elsewhereDrive = Run(Path.Combine(_base, "x"), outputFolderOption: Path.Combine(_base, "Lab"));
            Make().OutputRootIsDriveRoot(elsewhereDrive).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_folder_with_the_runs_own_marker_is_deleted_and_the_marker_goes_with_it_without_being_counted()
    {
        var folder = MakeRunFolder();
        Make().PlanDelete(Run(folder)).ShouldBe(LocalDeletePlan.Proceed);

        var result = Make().DeleteOutputFolder(Run(folder));

        result.ShouldBe(new LocalDeleteResult(LocalDeleteStatus.Deleted, FilesDeleted: 1, FilesRemaining: 0));
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void A_marker_naming_another_job_is_refused_with_nothing_deleted_and_asks_nothing()
    {
        var folder = MakeRunFolder(marker: "some-other-job");

        Make().PlanDelete(Run(folder)).ShouldBe(LocalDeletePlan.OtherRunsFolder);
        Make().DeleteOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.OtherRunsFolder);
        Make().DeleteUnmarkedOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.OtherRunsFolder, "a confirmation never unlocks another run's folder");

        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_with_no_marker_is_not_deleted_silently_it_needs_the_users_confirmation()
    {
        var folder = MakeRunFolder(marker: null);

        Make().PlanDelete(Run(folder)).ShouldBe(LocalDeletePlan.AskFirst);
        Make().DeleteOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.NeedsConfirmation);

        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public void After_the_user_confirms_a_folder_with_no_marker_is_deleted()
    {
        var folder = MakeRunFolder(marker: null);

        var result = Make().DeleteUnmarkedOutputFolder(Run(folder));

        result.Status.ShouldBe(LocalDeleteStatus.Deleted);
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void A_damaged_row_naming_Documents_inside_the_users_profile_never_deletes_documents_without_the_confirmation()
    {
        // Issue #934: OutputFolder = the profile, OutputDir = Documents inside it. Containment (taken from the same row) passes; only the marker can tell.
        var profile = Path.Combine(_base, "bob");
        var documents = Path.Combine(profile, "Documents");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "thesis.docx"), "keep");
        var run = Run(documents, outputFolderOption: profile);

        Make().PlanDelete(run).ShouldBe(LocalDeletePlan.AskFirst);
        Make().DeleteOutputFolder(run).Status.ShouldBe(LocalDeleteStatus.NeedsConfirmation);

        File.ReadAllText(Path.Combine(documents, "thesis.docx")).ShouldBe("keep");
        Directory.Exists(documents).ShouldBeTrue();

        // And once the user's shell folders are known, not even Continue deletes it.
        MakeWithShell().PlanDelete(run).ShouldBe(LocalDeletePlan.ProtectedFolder);
        MakeWithShell().DeleteUnmarkedOutputFolder(run).Status.ShouldBe(LocalDeleteStatus.ProtectedFolderRefused);
        File.ReadAllText(Path.Combine(documents, "thesis.docx")).ShouldBe("keep");
    }

    [Fact]
    public void A_partial_delete_keeps_the_marker_so_the_retry_does_not_ask_again()
    {
        var folder = MakeRunFolder();
        using var held = new FileStream(Path.Combine(folder, "a.bedgraph"), FileMode.Open, FileAccess.Read, FileShare.None);

        Make().DeleteOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.InUse);

        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue();
        Make().PlanDelete(Run(folder)).ShouldBe(LocalDeletePlan.Proceed);
    }

    [Fact]
    public void Planning_a_missing_or_blank_folder_proceeds_so_the_delete_itself_reports_it()
    {
        Make().PlanDelete(Run(Path.Combine(_root, "gone"))).ShouldBe(LocalDeletePlan.Proceed);
        Make().PlanDelete(Run(null)).ShouldBe(LocalDeletePlan.Proceed);
        Make().PlanDelete(new RunRecord("legacy", JobPhase.Completed, DateTimeOffset.UtcNow, OutputDir: MakeRunFolder("proto"), Imported: true)).ShouldBe(LocalDeletePlan.Proceed);
    }

    // ---- issue #934 cold review: the user's own folders are never deletable without a marker, whatever the user confirms ----

    private LocalRunFiles MakeWithShell(Action<string>? beforeFirstDelete = null)
    {
        // A pretend profile under the test folder: user-home paths are built at runtime, never written out.
        var profile = Path.Combine(_base, "bob");
        return new LocalRunFiles(
            _inputs,
            () => _root,
            [_appData],
            () => new ShellFolderSet(
                [profile, Path.Combine(profile, "Desktop"), Path.Combine(profile, "Documents"), Path.Combine(profile, "Pictures"), Path.Combine(profile, "Music"), Path.Combine(profile, "Videos")],
                [Path.Combine(profile, "Downloads")]),
            beforeFirstDelete);
    }

    public static TheoryData<string[], bool> ShellCases() => new()
    {
        { ["Documents"], true },
        { ["Desktop"], true },
        { ["Pictures"], true },
        { ["Music"], true },
        { ["Videos"], true },
        { ["Downloads"], true },
        { ["Documents", "thesis"], true },
        { ["Desktop", "lab"], true },
        { ["Downloads", "run-1"], false },
        { ["Documents", "lab", "run-1"], false },
        { ["Misc"], true },
    };

    [Theory]
    [MemberData(nameof(ShellCases))]
    public void An_unmarked_folder_that_is_a_shell_folder_or_directly_inside_one_is_refused_and_no_confirmation_unlocks_it(string[] relative, bool protectedFolder)
    {
        var profile = Path.Combine(_base, "bob");
        var folder = Path.Combine([profile, .. relative]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "thesis.docx"), "keep");
        var run = Run(folder, outputFolderOption: profile);
        var shell = MakeWithShell();

        shell.PlanDelete(run).ShouldBe(protectedFolder ? LocalDeletePlan.ProtectedFolder : LocalDeletePlan.AskFirst);
        var result = shell.DeleteUnmarkedOutputFolder(run);

        if (protectedFolder)
        {
            result.Status.ShouldBe(LocalDeleteStatus.ProtectedFolderRefused);
            File.ReadAllText(Path.Combine(folder, "thesis.docx")).ShouldBe("keep");
        }
        else
        {
            result.Status.ShouldBe(LocalDeleteStatus.Deleted);
        }
    }

    [Fact]
    public void A_marked_folder_inside_Documents_still_proceeds_and_an_unmarked_child_of_the_profile_is_protected()
    {
        var profile = Path.Combine(_base, "bob");
        var documents = Path.Combine(profile, "Documents");
        var marked = Path.Combine(documents, "run-1");
        Directory.CreateDirectory(marked);
        RunFolderMarker.Write(marked, "job-1");
        File.WriteAllText(Path.Combine(marked, "a.bedgraph"), "x");

        // Even though it sits in Documents, this run's own marker proves the app made it.
        MakeWithShell().PlanDelete(Run(marked, outputFolderOption: documents)).ShouldBe(LocalDeletePlan.Proceed);
        MakeWithShell().DeleteOutputFolder(Run(marked, outputFolderOption: documents)).Status.ShouldBe(LocalDeleteStatus.Deleted);

        // A folder directly inside the profile, with no marker, is protected (output folder = the test base, so containment passes).
        Directory.CreateDirectory(Path.Combine(profile, "Misc"));
        MakeWithShell().PlanDelete(Run(Path.Combine(profile, "Misc"), outputFolderOption: _base)).ShouldBe(LocalDeletePlan.ProtectedFolder);
    }

    // ---- issue #934 cold review: the disk can change between the plan and the first delete ----

    [Fact]
    public void A_marker_swapped_for_another_runs_between_the_plan_and_the_delete_stops_the_delete()
    {
        var folder = MakeRunFolder();
        var files = MakeWithShellAndHook(f => File.WriteAllText(Path.Combine(f, RunFolderMarker.FileName), "some-other-job\n"));

        files.PlanDelete(Run(folder)).ShouldBe(LocalDeletePlan.Proceed);
        var result = files.DeleteOutputFolder(Run(folder));

        result.Status.ShouldBe(LocalDeleteStatus.OtherRunsFolder);
        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue("nothing was deleted");
    }

    [Fact]
    public void A_marker_removed_between_the_plan_and_the_delete_stops_the_delete_and_asks_again()
    {
        var folder = MakeRunFolder();
        var files = MakeWithShellAndHook(f => File.Delete(Path.Combine(f, RunFolderMarker.FileName)));

        var result = files.DeleteOutputFolder(Run(folder));

        result.Status.ShouldBe(LocalDeleteStatus.NeedsConfirmation);
        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue("nothing was deleted");
    }

    [Fact]
    public void A_marker_planted_between_the_plan_and_the_confirmed_delete_stops_the_delete()
    {
        var folder = MakeRunFolder(marker: null);
        var files = MakeWithShellAndHook(f => File.WriteAllText(Path.Combine(f, RunFolderMarker.FileName), "some-other-job\n"));

        files.DeleteUnmarkedOutputFolder(Run(folder)).Status.ShouldBe(LocalDeleteStatus.OtherRunsFolder);
        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
    }

    [Fact]
    public void A_junction_swapped_in_between_the_plan_and_the_delete_stops_the_delete()
    {
        var folder = MakeRunFolder();
        var target = MakePreciousFolder();
        var sub = Path.Combine(folder, "keep-sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "k.txt"), "k");
        var swapped = false;
        var files = MakeWithShellAndHook(f =>
        {
            // The whole run folder becomes a link to somewhere else after it was listed.
            Directory.Move(f, f + "-moved");
            MakeJunction(f, target);
            swapped = true;
        });

        var result = files.DeleteOutputFolder(Run(folder));

        swapped.ShouldBeTrue();
        result.Status.ShouldBe(LocalDeleteStatus.LinkRefused);
        File.Exists(Path.Combine(target, "precious.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(folder + "-moved", "a.bedgraph")).ShouldBeTrue();
    }

    private LocalRunFiles MakeWithShellAndHook(Action<string> hook) => MakeWithShell(hook);

    [Fact]
    public void An_ordinary_subfolder_output_root_still_deletes_its_run_folder()
    {
        var folder = MakeRunFolder();

        Make().DeleteOutputFolder(Run(folder, outputFolderOption: _root)).Status.ShouldNotBe(LocalDeleteStatus.Refused);
        Directory.Exists(folder).ShouldBeFalse();
    }
    // The run's own folder as the app makes it: the marker names job-1 (the id Run() gives its row) unless a test says otherwise.
    private string MakeRunFolder(string name = "sample", string? marker = "job-1")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.bedgraph"), "x");
        if (marker is not null)
        {
            File.WriteAllText(Path.Combine(folder, RunFolderMarker.FileName), marker + "\n");
        }

        return folder;
    }

    [Fact]
    public void An_imported_prototype_run_is_never_deleted_because_its_folder_is_the_users_own()
    {
        // MEASURED 2026-10-16 by reading the code: an imported row has no options, so RunOutputRoot falls back to the Downloads folder and
        // the lexical "strictly inside" test passes. Hard Rule 14: the user's own prototype folder must survive anyway.
        var folder = MakeRunFolder("proto-run");
        var imported = new RunRecord("legacy-abc", JobPhase.Completed, DateTimeOffset.UtcNow, OutputDir: folder, Imported: true);

        Make().DeleteOutputFolder(imported).Status.ShouldBe(LocalDeleteStatus.Refused);

        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
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
        RunFolderMarker.Write(folder, "job-1");

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

    [Fact]
    public void Rerun_of_a_paired_run_uses_the_app_copy_of_the_gff3_then_the_original_then_nothing()
    {
        var original = Path.Combine(_base, "original.gff3");
        File.WriteAllText(original, "x");
        var staged = Path.Combine(_base, "staged.gff3");
        File.WriteAllText(staged, "x");
        var options = new RunOptions { ModelId = "m", RunTarget = "cloud", AnnotationPath = original };
        var files = Make();

        _inputs.FindAnnotationAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<StagedInput?>(new StagedInput(staged, "staged.gff3", IsAnnotation: true)));
        files.FindRerunAnnotation(Run(null), options).ShouldBe(staged);

        _inputs.FindAnnotationAsync("job-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<StagedInput?>(null));
        files.FindRerunAnnotation(Run(null), options).ShouldBe(original);

        File.Delete(original);
        files.FindRerunAnnotation(Run(null), options).ShouldBeNull();
        files.FindRerunAnnotation(Run(null), options with { AnnotationPath = null }).ShouldBeNull();
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
