using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #1047: <see cref="RunOutputFolders.CreateMarked"/> brings back a run's recorded folder with its marker, and leaves nothing behind when the marker cannot be written.</summary>
public sealed class RunOutputFoldersCreateMarkedTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "deg-createmarked-" + Guid.NewGuid().ToString("N"));

    public RunOutputFoldersCreateMarkedTests() => Directory.CreateDirectory(_parent);

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
    public void A_missing_folder_is_made_with_the_marker_of_the_job()
    {
        var folder = Path.Combine(_parent, "a", "b");

        RunOutputFolders.CreateMarked(folder, "job-1");

        RunFolderMarker.Check(folder, "job-1").ShouldBe(RunFolderMarkerState.Matches);
    }

    [Fact]
    public void A_marker_that_cannot_be_written_leaves_none_of_the_folders_it_made()
    {
        var folder = Path.Combine(_parent, "a", "b");

        Should.Throw<IOException>(() => RunOutputFolders.CreateMarked(folder, "job-1", (_, _) => throw new IOException("disk full")));

        Directory.Exists(Path.Combine(_parent, "a")).ShouldBeFalse();
        Directory.Exists(_parent).ShouldBeTrue("only what this call made is removed");
    }
}