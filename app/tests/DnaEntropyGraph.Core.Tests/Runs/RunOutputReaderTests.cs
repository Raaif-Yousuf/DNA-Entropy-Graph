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

    [Fact]
    public void Never_writes_anything_into_the_folder()
    {
        Put("two/two.gb", "LOCUS");
        var before = Directory.EnumerateFileSystemEntries(_folder, "*", SearchOption.AllDirectories).Order().ToList();

        new RunOutputReader().Read(_folder);

        Directory.EnumerateFileSystemEntries(_folder, "*", SearchOption.AllDirectories).Order().ShouldBe(before);
    }
}
