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
    public void UniqueFilePath_numbers_a_taken_name_in_brackets_and_counts_a_folder_of_that_name_as_taken()
    {
        Directory.CreateDirectory(_parent);
        RunOutputFolders.UniqueFilePath(_parent, "chr1.entropy", ".png").ShouldBe(Path.Combine(_parent, "chr1.entropy.png"));

        File.WriteAllText(Path.Combine(_parent, "chr1.entropy.png"), "x");
        Directory.CreateDirectory(Path.Combine(_parent, "chr1.entropy (2).png"));

        RunOutputFolders.UniqueFilePath(_parent, "chr1.entropy", ".png").ShouldBe(Path.Combine(_parent, "chr1.entropy (3).png"));
    }

    [Fact]
    public void ExportsFolder_is_one_deterministic_safe_name_per_run_and_creates_nothing()
    {
        var first = RunOutputFolders.ExportsFolder(_parent, "my:run", "job-1");
        var second = RunOutputFolders.ExportsFolder(_parent, "my:run", "job-1");

        second.ShouldBe(first);
        Path.GetDirectoryName(first).ShouldBe(_parent);
        Path.GetFileName(first).ShouldStartWith("my_run exports ");
        Path.GetFileName(first).ShouldNotContain(":");
        Directory.Exists(_parent).ShouldBeFalse("naming a folder must not create it");
    }

    [Fact]
    public void ExportsFolder_differs_for_two_runs_with_the_same_name_and_is_never_the_plain_name_a_user_may_own()
    {
        var a = RunOutputFolders.ExportsFolder(_parent, "SetTnpB", "job-a");
        var b = RunOutputFolders.ExportsFolder(_parent, "SetTnpB", "job-b");

        a.ShouldNotBe(b);
        a.ShouldNotBe(Path.Combine(_parent, "SetTnpB exports"));
    }

    [Fact]
    public void CreateFolderChain_returns_only_what_it_made_and_RemoveCreated_takes_exactly_those_away()
    {
        Directory.CreateDirectory(_parent);
        var existing = Path.Combine(_parent, "already");
        Directory.CreateDirectory(existing);

        var none = RunOutputFolders.CreateFolderChain(existing);
        var made = RunOutputFolders.CreateFolderChain(Path.Combine(existing, "a", "b"));

        none.ShouldBeEmpty();
        made.ShouldBe([Path.Combine(existing, "a"), Path.Combine(existing, "a", "b")]);
        Directory.Exists(Path.Combine(existing, "a", "b")).ShouldBeTrue();

        RunOutputFolders.RemoveCreated(made);

        Directory.Exists(Path.Combine(existing, "a")).ShouldBeFalse();
        Directory.Exists(existing).ShouldBeTrue("a folder that was already there is never removed");
    }

    [Fact]
    public void RemoveCreated_leaves_a_folder_that_has_something_in_it_and_removes_the_empty_one_inside_it()
    {
        // Issue #922: the old assertion (a file survives) passed against a no-op. Here the empty inner folder must go and the busy outer one must stay.
        var made = RunOutputFolders.CreateFolderChain(Path.Combine(_parent, "x", "y"));
        File.WriteAllText(Path.Combine(_parent, "x", "keep.txt"), "k");

        RunOutputFolders.RemoveCreated(made);

        Directory.Exists(Path.Combine(_parent, "x", "y")).ShouldBeFalse("the empty folder the call made is taken away");
        File.Exists(Path.Combine(_parent, "x", "keep.txt")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_parent, "x")).ShouldBeTrue("a folder with something in it is never removed");
    }

    [Fact]
    public void CreateFolderChain_counts_a_trailing_separator_once()
    {
        Directory.CreateDirectory(_parent);

        var made = RunOutputFolders.CreateFolderChain(Path.Combine(_parent, "leaf") + Path.DirectorySeparatorChar);

        made.ShouldBe([Path.Combine(_parent, "leaf")]);
    }

    [Fact]
    public void CreateFolderChain_with_a_required_root_that_is_missing_creates_nothing_and_says_so()
    {
        var root = Path.Combine(_parent, "deleted-downloads");

        Should.Throw<DirectoryNotFoundException>(() => RunOutputFolders.CreateFolderChain(Path.Combine(root, "exports"), root));

        Directory.Exists(root).ShouldBeFalse("a missing export root is not brought back");
    }

    [Fact]
    public void CreateFolderChain_with_a_required_root_that_exists_makes_only_the_leaf()
    {
        Directory.CreateDirectory(_parent);

        var made = RunOutputFolders.CreateFolderChain(Path.Combine(_parent, "leaf"), _parent + Path.DirectorySeparatorChar);

        made.ShouldBe([Path.Combine(_parent, "leaf")]);
    }

    [Fact]
    public void A_failed_CreateDirectory_part_way_removes_the_parents_it_already_made_and_rethrows()
    {
        Directory.CreateDirectory(_parent);
        var target = Path.Combine(_parent, "a", "b", "c");
        var calls = 0;

        Should.Throw<IOException>(() => RunOutputFolders.CreateFolderChain(target, null, Directory.Exists, dir =>
        {
            if (++calls == 3)
            {
                throw new IOException("disk full");
            }

            Directory.CreateDirectory(dir);
        }));

        calls.ShouldBe(3);
        Directory.Exists(Path.Combine(_parent, "a")).ShouldBeFalse("both folders were made by this call and are taken away");
        Directory.Exists(_parent).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_that_exists_but_cannot_be_probed_is_not_claimed_as_made()
    {
        // Directory.Exists answers false when access is denied, so a pre-existing empty folder would be listed as made and then removed.
        RunOutputFolders.FolderExists(@"C:\locked", _ => throw new UnauthorizedAccessException()).ShouldBeTrue();
        RunOutputFolders.FolderExists(@"C:\none", _ => throw new DirectoryNotFoundException()).ShouldBeFalse();
        RunOutputFolders.FolderExists(@"C:\none", _ => throw new FileNotFoundException()).ShouldBeFalse();
        RunOutputFolders.FolderExists(@"C:\there", _ => FileAttributes.Directory).ShouldBeTrue();

        var existing = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(_parent) };
        var created = new List<string>();
        var made = RunOutputFolders.CreateFolderChain(Path.Combine(_parent, "locked"), null, dir => existing.Contains(dir) || dir.EndsWith("locked", StringComparison.Ordinal), created.Add);

        made.ShouldBeEmpty("the probe says it is there, so nothing is claimed");
        created.ShouldBeEmpty();
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_template_falls_back_to_the_file_name_not_run(string template)
        => RunOutputFolders.RunFolderName(Options(template), new Core.Contract.StagedInput("x/SetTnpB.fasta", "SetTnpB.fasta"), Clock).ShouldBe("SetTnpB");

    [Fact]
    public void A_stem_over_100_characters_is_cut_to_100()
    {
        var name = RunOutputFolders.RunFolderName(Options("{file}"), new Core.Contract.StagedInput("x/long.fasta", new string('a', 150) + ".fasta"), Clock);

        name.ShouldBe(new string('a', 100));
    }

    [Fact]
    public void A_device_name_as_the_file_is_renamed_so_Windows_can_create_it()
        => RunOutputFolders.RunFolderName(Options("{file}"), new Core.Contract.StagedInput("x/CON.fasta", "CON.fasta"), Clock).ShouldBe("run_CON");

    [Fact]
    public void An_accession_input_is_named_by_its_output_stem_not_cut_at_the_version_dot()
    {
        var input = Core.Contract.StagedInput.ForAccession("NC_045512.2", "100-500");

        RunOutputFolders.RunFolderName(Options("{file}_{model}"), input, Clock).ShouldBe("NC_045512_2_100-500_evo2_7b");
    }
}
