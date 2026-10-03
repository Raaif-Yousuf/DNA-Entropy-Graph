using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Tests.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

/// <summary>Issue #102: what the Results page reads from a finished run's own output folder.</summary>
public sealed class RunOutputReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deg-results-tests", Guid.NewGuid().ToString("N"));

    public RunOutputReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Put(string relative, string text)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string FixtureText() => File.ReadAllText(ContractFixtures.Path("run_summary_two_contigs.txt"));

    [Fact]
    public void A_folder_that_does_not_exist_reads_as_null()
    {
        new RunOutputReader().Read(Path.Combine(_folder, "gone")).ShouldBeNull();
    }

    [Fact]
    public void Lists_every_file_below_the_folder_in_name_order_with_its_size()
    {
        Put("b/two.fasta", ">x\nACGT\n");
        Put("a/one.gb", "LOCUS");
        Put("provenance.json", "{}");

        var snapshot = new RunOutputReader().Read(_folder)!;

        snapshot.Files.Select(f => f.RelativePath).ShouldBe(["a/one.gb", "b/two.fasta", "provenance.json"]);
        snapshot.Files[0].Bytes.ShouldBe(5);
        snapshot.Files[0].FullPath.ShouldBe(Path.Combine(_folder, "a", "one.gb"));
    }

    [Fact]
    public void Reads_the_summary_the_worker_wrote_and_nothing_else()
    {
        Put("two/two.summary.txt", FixtureText());
        Put("two/two.gb", "LOCUS");

        var snapshot = new RunOutputReader().Read(_folder)!;

        var only = snapshot.Summaries.ShouldHaveSingleItem();
        only.RelativePath.ShouldBe("two/two.summary.txt");
        only.Summary!.MeanAll.ShouldBe(1.6341, 1e-9);
    }

    [Fact]
    public void A_genbank_run_summary_named_stats_txt_is_found_too()
    {
        Put("rec/stats.txt", FixtureText());

        new RunOutputReader().Read(_folder)!.Summaries.ShouldHaveSingleItem().RelativePath.ShouldBe("rec/stats.txt");
    }

    [Fact]
    public void A_summary_that_cannot_be_understood_is_listed_with_no_numbers_rather_than_dropped()
    {
        Put("two/two.summary.txt", "garbage");

        new RunOutputReader().Read(_folder)!.Summaries.ShouldHaveSingleItem().Summary.ShouldBeNull();
    }

    [Fact]
    public void A_run_with_no_summary_file_has_no_summaries()
    {
        Put("two/two.gb", "LOCUS");

        new RunOutputReader().Read(_folder)!.Summaries.ShouldBeEmpty();
    }

    // A OneDrive "online-only" file is a reparse point whose bytes are in the cloud: still the user's own result file.
    private const FileAttributes Placeholder = FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.Offline | RunOutputReader.RecallOnDataAccess;

    private RunOutputReader WithAttributes(Func<string, FileAttributes> attributesOf, Func<string, string?>? linkTarget = null, Func<string, long?>? sizeOf = null)
        => new(attributesOf, linkTarget ?? (_ => null), sizeOf);

    [Theory]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Offline)]
    [InlineData(FileAttributes.ReparsePoint | RunOutputReader.RecallOnDataAccess)]
    [InlineData(FileAttributes.ReparsePoint | RunOutputReader.RecallOnOpen)]
    [InlineData(Placeholder)]
    public void A_cloud_placeholder_file_is_listed_not_skipped(FileAttributes attributes)
    {
        Put("two/two.gb", "LOCUS");
        var target = Path.Combine(_folder, "two", "two.gb");
        var reader = WithAttributes(p => p == target ? attributes : File.GetAttributes(p));

        reader.Read(_folder)!.Files.Select(f => f.RelativePath).ShouldBe(["two/two.gb"]);
    }

    [Fact]
    public void A_link_to_a_file_outside_the_run_folder_is_refused()
    {
        Put("two/two.gb", "LOCUS");
        var link = Path.Combine(_folder, "two", "two.gb");
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else.txt");
        var reader = WithAttributes(p => p == link ? FileAttributes.ReparsePoint : File.GetAttributes(p), p => p == link ? outside : null);

        reader.Read(_folder)!.Files.ShouldBeEmpty();
    }

    [Fact]
    public void A_link_to_a_file_inside_the_run_folder_is_listed()
    {
        Put("two/two.gb", "LOCUS");
        Put("two/real.gb", "LOCUS");
        var link = Path.Combine(_folder, "two", "two.gb");
        var inside = Path.Combine(_folder, "two", "real.gb");
        var reader = WithAttributes(p => p == link ? FileAttributes.ReparsePoint : File.GetAttributes(p), p => p == link ? inside : null);

        reader.Read(_folder)!.Files.Select(f => f.RelativePath).ShouldBe(["two/real.gb", "two/two.gb"]);
    }

    [Fact]
    public void A_reparse_point_with_no_link_target_is_a_placeholder_and_is_listed()
    {
        // No resolvable link target means it is not a symlink or mount point, whatever the attribute bits say.
        Put("two/two.gb", "LOCUS");
        var file = Path.Combine(_folder, "two", "two.gb");
        var reader = WithAttributes(p => p == file ? FileAttributes.ReparsePoint : File.GetAttributes(p));

        reader.Read(_folder)!.Files.Select(f => f.RelativePath).ShouldBe(["two/two.gb"]);
    }

    public static TheoryData<FileAttributes> OfflineBitCombinations() => new()
    {
        FileAttributes.ReparsePoint,
        FileAttributes.ReparsePoint | FileAttributes.Offline,
        FileAttributes.ReparsePoint | RunOutputReader.RecallOnOpen,
        FileAttributes.ReparsePoint | RunOutputReader.RecallOnDataAccess,
        FileAttributes.ReparsePoint | FileAttributes.Offline | RunOutputReader.RecallOnOpen | RunOutputReader.RecallOnDataAccess,
    };

    [Fact]
    public void The_offline_bit_combinations_cover_every_recall_bit()
    {
        var all = OfflineBitCombinations().Select(d => d.Data).Aggregate((a, b) => a | b);

        all.HasFlag(FileAttributes.Offline).ShouldBeTrue();
        all.HasFlag(RunOutputReader.RecallOnOpen).ShouldBeTrue();
        all.HasFlag(RunOutputReader.RecallOnDataAccess).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(OfflineBitCombinations))]
    public void A_link_to_a_file_outside_the_folder_is_refused_whatever_attribute_bits_it_carries(FileAttributes attributes)
    {
        Put("two/two.gb", "LOCUS");
        var link = Path.Combine(_folder, "two", "two.gb");
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else.txt");
        var reader = WithAttributes(p => p == link ? attributes : File.GetAttributes(p), p => p == link ? outside : null);

        reader.Read(_folder)!.Files.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(OfflineBitCombinations))]
    public void A_link_to_a_file_inside_the_folder_is_listed_whatever_attribute_bits_it_carries(FileAttributes attributes)
    {
        Put("two/two.gb", "LOCUS");
        Put("two/real.gb", "LOCUS");
        var link = Path.Combine(_folder, "two", "two.gb");
        var inside = Path.Combine(_folder, "two", "real.gb");
        var reader = WithAttributes(p => p == link ? attributes : File.GetAttributes(p), p => p == link ? inside : null);

        reader.Read(_folder)!.Files.Select(f => f.RelativePath).ShouldBe(["two/real.gb", "two/two.gb"]);
    }

    [Fact]
    public void A_linked_folder_is_not_entered_but_a_placeholder_folder_is()
    {
        Put("linked/a.gb", "LOCUS");
        Put("cloud/b.gb", "LOCUS");
        var linked = Path.Combine(_folder, "linked");
        var cloud = Path.Combine(_folder, "cloud");
        var reader = WithAttributes(
            p =>
                p == linked ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : p == cloud ? FileAttributes.Directory | FileAttributes.ReparsePoint | RunOutputReader.RecallOnDataAccess
                : File.GetAttributes(p),
            p => p == linked ? Path.GetTempPath() : null);

        reader.Read(_folder)!.Files.Select(f => f.RelativePath).ShouldBe(["cloud/b.gb"]);
    }

    [Theory]
    [MemberData(nameof(OfflineBitCombinations))]
    public void A_junction_carrying_offline_bits_is_never_entered_even_when_it_points_at_the_parent(FileAttributes attributes)
    {
        Put("loop/a.gb", "LOCUS");
        Put("keep.gb", "LOCUS");
        var junction = Path.Combine(_folder, "loop");
        var parent = Path.GetDirectoryName(_folder)!;
        var reader = WithAttributes(
            p => p == junction ? FileAttributes.Directory | attributes : File.GetAttributes(p),
            p => p == junction ? parent : null);

        reader.Read(_folder)!.Files.Select(f => f.RelativePath).ShouldBe(["keep.gb"]);
    }

    [Fact]
    public void A_linked_folder_inside_the_run_folder_is_not_entered_either()
    {
        Put("loop/a.gb", "LOCUS");
        var junction = Path.Combine(_folder, "loop");
        var reader = WithAttributes(
            p => p == junction ? FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline : File.GetAttributes(p),
            p => p == junction ? _folder : null);

        reader.Read(_folder)!.Files.ShouldBeEmpty();
    }

    [Fact]
    public void A_folder_tree_deeper_than_any_run_makes_stops_at_a_depth_cap()
    {
        var relative = string.Join('/', Enumerable.Repeat("d", RunOutputReader.MaxDepth + 8)) + "/deep.gb";
        Put("top.gb", "LOCUS");
        Put(relative, "LOCUS");

        var files = new RunOutputReader().Read(_folder)!.Files.Select(f => f.RelativePath).ToList();

        files.ShouldContain("top.gb");
        files.ShouldNotContain(relative);
    }

    [Fact]
    public void A_file_at_the_deepest_allowed_level_is_still_listed()
    {
        var relative = string.Join('/', Enumerable.Repeat("d", RunOutputReader.MaxDepth)) + "/ok.gb";
        Put(relative, "LOCUS");

        new RunOutputReader().Read(_folder)!.Files.Select(f => f.RelativePath).ShouldBe([relative]);
    }

    [Fact]
    public void A_size_that_cannot_be_read_is_unknown_not_zero()
    {
        Put("two/two.gb", "LOCUS");
        Put("two/other.gb", "LOCUS");
        var unreachable = Path.Combine(_folder, "two", "two.gb");
        var reader = WithAttributes(File.GetAttributes, sizeOf: p => p == unreachable ? null : 5);

        var files = reader.Read(_folder)!.Files.ToDictionary(f => f.RelativePath);

        files["two/two.gb"].Bytes.ShouldBeNull();
        files["two/other.gb"].Bytes.ShouldBe(5);
    }

    [Fact]
    public void A_readable_file_reports_its_real_size()
    {
        Put("two/two.gb", "LOCUS");

        new RunOutputReader().Read(_folder)!.Files.Single().Bytes.ShouldBe(5);
    }

    // Real-disk checks of the production constructor: every test above injects the link seam, so none of them
    // proves the real FileInfo/ResolveLinkTarget call reports a junction or symlink as a link.
    private string MakeRunBesideAnOutsideFolder(out string outsideFile)
    {
        var run = Path.Combine(_folder, "run");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "keep.gb"), "LOCUS");
        var outside = Path.Combine(_folder, "outside");
        Directory.CreateDirectory(outside);
        outsideFile = Path.Combine(outside, "secret.gb");
        File.WriteAllText(outsideFile, "LOCUS");
        return run;
    }

    private static void MakeJunction(string link, string target)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are Windows only");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Directory.Exists(link).ShouldBeTrue("mklink /J did not create the junction");
        new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint).ShouldBeTrue();
    }

    // Removes the link itself, never what it points at.
    private static void DropLink(string link)
    {
        if (Directory.Exists(link) || File.Exists(link))
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
            else
            {
                File.Delete(link);
            }
        }
    }

    [Fact]
    public void A_real_junction_to_the_run_folders_parent_is_not_entered_and_the_read_terminates()
    {
        var run = MakeRunBesideAnOutsideFolder(out _);
        var loop = Path.Combine(run, "loop");
        try
        {
            MakeJunction(loop, _folder);

            var files = new RunOutputReader().Read(run)!.Files.Select(f => f.RelativePath).ToList();

            files.ShouldBe(["keep.gb"]);
        }
        finally
        {
            DropLink(loop);
        }
    }

    [Fact]
    public void A_real_junction_to_a_folder_outside_the_run_lists_nothing_from_it()
    {
        var run = MakeRunBesideAnOutsideFolder(out _);
        var outside = Path.Combine(_folder, "outside");
        var link = Path.Combine(run, "out");
        try
        {
            MakeJunction(link, outside);

            var files = new RunOutputReader().Read(run)!.Files.Select(f => f.RelativePath).ToList();

            files.ShouldBe(["keep.gb"]);
        }
        finally
        {
            DropLink(link);
        }
    }

    [Fact]
    public void A_real_file_symlink_to_an_outside_file_is_not_listed_but_one_to_an_inside_file_is()
    {
        var run = MakeRunBesideAnOutsideFolder(out var outsideFile);
        var toOutside = Path.Combine(run, "leak.gb");
        var toInside = Path.Combine(run, "alias.gb");
        try
        {
            try
            {
                File.CreateSymbolicLink(toOutside, outsideFile);
                File.CreateSymbolicLink(toInside, Path.Combine(run, "keep.gb"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Skip("creating a file symlink needs Developer Mode or an elevated session: " + ex.GetType().Name);
            }

            var files = new RunOutputReader().Read(run)!.Files.Select(f => f.RelativePath).ToList();

            files.ShouldBe(["alias.gb", "keep.gb"]);
        }
        finally
        {
            DropLink(toOutside);
            DropLink(toInside);
        }
    }

    [Fact]
    public void Never_writes_anything_into_the_folder()
    {
        Put("two/two.gb", "LOCUS");
        var before = Directory.EnumerateFileSystemEntries(_folder, "*", SearchOption.AllDirectories).Order().ToList();

        new RunOutputReader().Read(_folder);

        Directory.EnumerateFileSystemEntries(_folder, "*", SearchOption.AllDirectories).Order().ShouldBe(before);
    }
}
