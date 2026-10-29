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

    [Fact]
    public void In_portable_mode_the_default_output_parent_is_the_portable_outputs_folder_and_downloads_is_never_asked()
    {
        var asked = false;

        var parent = RunOutputFolders.DefaultParent(() => { asked = true; return @"C:\Users\me\Downloads"; }, portableOutputs: @"D:\stick\outputs");

        parent.ShouldBe(@"D:\stick\outputs");
        asked.ShouldBeFalse();
        RunOutputFolders.DefaultParent(() => @"C:\Users\me\Downloads", portableOutputs: null).ShouldBe(@"C:\Users\me\Downloads");
    }

    // ---- Issue #514: the run folder is named from RunOptions.NameTemplate, by one function both runners and the page use ----

    private static readonly DateTime Clock = new(2026, 10, 29, 14, 5, 0);

    private static RunOptions Options(string template) => new() { ModelId = "evo2_7b", RunTarget = "Cloud", NameTemplate = template };

    [Theory]
    [InlineData("{file}", "SetTnpB")]
    [InlineData("{file}_{model}", "SetTnpB_evo2_7b")]
    [InlineData("{date}_{file}", "2026-10-29_SetTnpB")]
    [InlineData("{file}-{time}-{n}", "SetTnpB-1405-1")]
    public void The_run_folder_name_expands_the_template(string template, string expected)
        => RunOutputFolders.RunFolderName(Options(template), new Core.Contract.StagedInput("x/SetTnpB.fasta", "SetTnpB.fasta"), Clock).ShouldBe(expected);

    [Fact]
    public void The_default_template_names_the_folder_exactly_as_the_file_stem_did_before()
    {
        var input = new Core.Contract.StagedInput("x/sample.v2.fa", "sample.v2.fa");

        RunOutputFolders.RunFolderName(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }, input, Clock).ShouldBe(input.OutputStem);
        input.OutputStem.ShouldBe("sample.v2");
    }

    [Fact]
    public void An_accession_input_is_named_by_its_output_stem_not_cut_at_the_version_dot()
    {
        var input = Core.Contract.StagedInput.ForAccession("NC_045512.2", "100-500");

        RunOutputFolders.RunFolderName(Options("{file}_{model}"), input, Clock).ShouldBe("NC_045512_2_100-500_evo2_7b");
    }
}
