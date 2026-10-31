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
    public void A_folder_holding_only_the_marker_counts_as_empty_and_is_removed_without_recursion()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-a");

        RunFolderMarker.IsEmptyApartFromMarker(folder).ShouldBeTrue();
        RunFolderMarker.TryRemoveEmptyFolder(folder).ShouldBeTrue();

        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void A_folder_with_a_real_file_is_not_empty_and_is_left_alone()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "sample", jobId: "job-a");
        File.WriteAllText(Path.Combine(folder, "a.bedgraph"), "x");

        RunFolderMarker.IsEmptyApartFromMarker(folder).ShouldBeFalse();
        RunFolderMarker.TryRemoveEmptyFolder(folder).ShouldBeFalse();

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
