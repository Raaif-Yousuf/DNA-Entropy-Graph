using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #460 / Hard Rule 14: results go to a fresh folder in the chosen output folder, never over an earlier run's.</summary>
public sealed class RunOutputFoldersTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "deg-out-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_parent))
        {
            Directory.Delete(_parent, recursive: true);
        }
    }

    [Fact]
    public void Creates_the_folder_named_after_the_input()
    {
        var folder = RunOutputFolders.CreateUnique(_parent, "SetTnpB");

        folder.ShouldBe(Path.Combine(_parent, "SetTnpB"));
        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public void A_name_that_already_exists_gets_a_numeric_suffix_and_the_old_folder_is_untouched()
    {
        var first = RunOutputFolders.CreateUnique(_parent, "SetTnpB");
        File.WriteAllText(Path.Combine(first, "keep.txt"), "x");

        var second = RunOutputFolders.CreateUnique(_parent, "SetTnpB");
        var third = RunOutputFolders.CreateUnique(_parent, "SetTnpB");

        second.ShouldBe(Path.Combine(_parent, "SetTnpB_2"));
        third.ShouldBe(Path.Combine(_parent, "SetTnpB_3"));
        File.ReadAllText(Path.Combine(first, "keep.txt")).ShouldBe("x");
    }

    [Fact]
    public void Concurrent_runs_with_the_same_input_name_never_share_a_folder()
    {
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ => paths.Add(RunOutputFolders.CreateUnique(_parent, "SetTnpB")));

        paths.Distinct().Count().ShouldBe(32, "two runs sharing a folder would write over each other's results (Hard Rule 14)");
        Directory.GetDirectories(_parent).Length.ShouldBe(32);
    }

    [Fact]
    public void A_plain_file_named_like_the_input_counts_as_taken_and_the_next_suffix_is_used()
    {
        Directory.CreateDirectory(_parent);
        File.WriteAllText(Path.Combine(_parent, "sample"), "a file with no extension");

        var folder = RunOutputFolders.CreateUnique(_parent, "sample");

        folder.ShouldBe(Path.Combine(_parent, "sample_2"));
        File.ReadAllText(Path.Combine(_parent, "sample")).ShouldBe("a file with no extension");
    }

    [Fact]
    public void A_failure_removing_the_staging_folder_never_stops_the_run_getting_a_folder()
    {
        Directory.CreateDirectory(_parent);
        File.WriteAllText(Path.Combine(_parent, "sample"), "x");

        var folder = RunOutputFolders.CreateUnique(_parent, "sample", _ => throw new UnauthorizedAccessException("denied"));

        folder.ShouldBe(Path.Combine(_parent, "sample_2"));
    }

    [Fact]
    public void A_folder_someone_else_created_is_never_returned()
    {
        Directory.CreateDirectory(Path.Combine(_parent, "SetTnpB"));
        Directory.CreateDirectory(Path.Combine(_parent, "SetTnpB_2"));

        RunOutputFolders.CreateUnique(_parent, "SetTnpB").ShouldBe(Path.Combine(_parent, "SetTnpB_3"));
    }

    [Theory]
    [InlineData("a:b*c?", "a_b_c_")]
    [InlineData("", "run")]
    [InlineData("   ", "run")]
    [InlineData("..", "run")]
    public void Unsafe_or_empty_names_are_made_safe(string name, string expected)
        => Path.GetFileName(RunOutputFolders.CreateUnique(_parent, name)).ShouldBe(expected);

    [Fact]
    public void The_default_parent_is_the_Downloads_known_folder_even_when_it_is_redirected()
    {
        var redirected = Path.Combine(_parent, "OneDrive", "Downloads");

        RunOutputFolders.DefaultParent(() => redirected).ShouldBe(redirected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_default_parent_falls_back_to_a_Downloads_folder_under_the_profile_only_when_the_lookup_gives_nothing(string? lookup)
    {
        var parent = RunOutputFolders.DefaultParent(() => lookup);

        Path.GetFileName(parent).ShouldBe("Downloads");
        Path.GetDirectoryName(parent).ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    public void The_default_parent_falls_back_when_the_lookup_throws()
        => Path.GetFileName(RunOutputFolders.DefaultParent(() => throw new InvalidOperationException("shell unavailable"))).ShouldBe("Downloads");
}
