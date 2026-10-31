using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

/// <summary>Issue #934: every run output folder the app creates carries a marker naming its job; a recursive delete requires it.</summary>
public sealed class RunFolderMarkerTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "deg-marker-" + Guid.NewGuid().ToString("N"));

    public RunFolderMarkerTests() => Directory.CreateDirectory(_parent);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void CreateUnique_with_a_job_id_writes_the_marker_holding_exactly_that_job_id()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-42");

        File.ReadAllText(Path.Combine(folder, RunFolderMarker.FileName)).Trim().ShouldBe("job-42");
        RunFolderMarker.Check(folder, "job-42").ShouldBe(RunFolderMarkerState.Matches);
    }

    [Fact]
    public void CreateUnique_without_a_job_id_writes_no_marker()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample");

        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeFalse();
        RunFolderMarker.Check(folder, "job-42").ShouldBe(RunFolderMarkerState.Absent);
    }

    [Fact]
    public void A_second_run_with_the_same_name_gets_its_own_folder_and_its_own_marker()
    {
        var first = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-a");
        var second = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-b");

        second.ShouldNotBe(first);
        RunFolderMarker.Check(first, "job-a").ShouldBe(RunFolderMarkerState.Matches);
        RunFolderMarker.Check(second, "job-b").ShouldBe(RunFolderMarkerState.Matches);
        RunFolderMarker.Check(first, "job-b").ShouldBe(RunFolderMarkerState.OtherRun);
    }

    [Fact]
    public void A_marker_naming_another_job_is_other_run_and_a_blank_job_id_never_matches()
    {
        var folder = Path.Combine(_parent, "f");
        Directory.CreateDirectory(folder);
        RunFolderMarker.Write(folder, "job-a");

        RunFolderMarker.Check(folder, "job-b").ShouldBe(RunFolderMarkerState.OtherRun);
        RunFolderMarker.Check(folder, "").ShouldBe(RunFolderMarkerState.OtherRun);
        RunFolderMarker.Check(folder, "JOB-A").ShouldBe(RunFolderMarkerState.OtherRun, "job ids are compared exactly");
    }

    [Fact]
    public void A_marker_that_is_a_folder_or_unreadable_is_never_trusted()
    {
        var folder = Path.Combine(_parent, "f");
        Directory.CreateDirectory(Path.Combine(folder, RunFolderMarker.FileName));

        RunFolderMarker.Check(folder, "job-a").ShouldBe(RunFolderMarkerState.OtherRun);
    }

    [Fact]
    public void A_marker_that_is_a_symbolic_link_is_never_trusted_even_when_its_target_names_the_job()
    {
        var folder = Path.Combine(_parent, "f");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(_parent, "elsewhere.txt");
        File.WriteAllText(target, "job-a\n");
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, RunFolderMarker.FileName), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("A symbolic link could not be created here (needs Developer Mode or administrator rights).");
        }

        RunFolderMarker.Check(folder, "job-a").ShouldBe(RunFolderMarkerState.OtherRun);
    }

    [Fact]
    public void A_marker_larger_than_512_bytes_is_never_trusted_even_when_it_starts_with_the_job_id()
    {
        var folder = Path.Combine(_parent, "f");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RunFolderMarker.FileName), "job-a\n" + new string('x', 600));

        RunFolderMarker.Check(folder, "job-a").ShouldBe(RunFolderMarkerState.OtherRun);
    }

    [Fact]
    public void A_marker_that_cannot_be_read_because_it_is_locked_is_other_run_not_absent()
    {
        var folder = Path.Combine(_parent, "f");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, RunFolderMarker.FileName);
        File.WriteAllText(path, "job-a\n");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        RunFolderMarker.Check(folder, "job-a").ShouldBe(RunFolderMarkerState.OtherRun, "only a missing file may be confirmed by the user");
    }

    [Fact]
    public void A_folder_holding_only_the_marker_counts_as_empty_and_is_removed_without_recursion()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-a");

        RunFolderMarker.IsEmptyApartFromMarker(folder).ShouldBeTrue();
        RunFolderMarker.TryRemoveEmptyFolder(folder, "job-a").ShouldBeTrue();

        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void A_folder_holding_only_another_runs_marker_is_kept()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-other");

        RunFolderMarker.TryRemoveEmptyFolder(folder, "job-a").ShouldBeFalse("another run's folder is not ours to remove, even when it is empty");

        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue();
        RunFolderMarker.Check(folder, "job-other").ShouldBe(RunFolderMarkerState.Matches);
    }

    [Fact]
    public void A_folder_with_a_marker_that_cannot_be_trusted_is_kept_and_an_empty_unmarked_folder_is_removed()
    {
        var oversized = Path.Combine(_parent, "oversized");
        Directory.CreateDirectory(oversized);
        File.WriteAllText(Path.Combine(oversized, RunFolderMarker.FileName), "job-a\n" + new string('x', 600));
        var unmarked = Path.Combine(_parent, "unmarked");
        Directory.CreateDirectory(unmarked);

        RunFolderMarker.TryRemoveEmptyFolder(oversized, "job-a").ShouldBeFalse();
        RunFolderMarker.TryRemoveEmptyFolder(unmarked, "job-a").ShouldBeTrue();

        Directory.Exists(oversized).ShouldBeTrue();
        Directory.Exists(unmarked).ShouldBeFalse();
    }

    [Fact]
    public void A_folder_with_a_real_file_is_not_empty_and_is_left_alone()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-a");
        File.WriteAllText(Path.Combine(folder, "a.bedgraph"), "x");

        RunFolderMarker.IsEmptyApartFromMarker(folder).ShouldBeFalse();
        RunFolderMarker.TryRemoveEmptyFolder(folder, "job-a").ShouldBeFalse();

        File.Exists(Path.Combine(folder, "a.bedgraph")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, RunFolderMarker.FileName)).ShouldBeTrue("a folder that is not removed keeps its marker");
    }

    [Fact]
    public void The_results_listing_never_shows_the_marker_as_an_output_file()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-a");
        File.WriteAllText(Path.Combine(folder, "a.bedgraph"), "x");

        new RunOutputReader().Read(folder)!.Files.Select(f => f.RelativePath).ShouldBe(["a.bedgraph"]);
    }
}
