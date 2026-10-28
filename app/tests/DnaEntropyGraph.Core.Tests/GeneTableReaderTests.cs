using DnaEntropyGraph.Core.Tracks;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #552: reading the worker's gene summary (writers/gene_summary.py) for the Results table.</summary>
public sealed class GeneTableReaderTests : IDisposable
{
    private const string Header = "contig\tgene_id\tbegin\tend\tstrand\tlength_nt\tpartial\twraps_origin\tsegments\tmean_entropy_bits\tmin_entropy_bits\tmax_entropy_bits\tmean_surprisal_bits\tfraction_low_entropy";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-genes", Guid.NewGuid().ToString("N"));

    public GeneTableReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, params string[] lines)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void A_row_is_read_with_numbers_in_the_invariant_culture()
    {
        var path = Write("a.genes.tsv", Header, "c1\tgeneA\t10\t99\t+\t90\tfalse\tfalse\t10..99\t1.2500\t0.5000\t1.9000\t\t0.1000");

        var rows = GeneTableReader.Read(path)!;

        var r = rows.Single();
        r.Contig.ShouldBe("c1");
        r.GeneId.ShouldBe("geneA");
        r.Begin.ShouldBe(10);
        r.End.ShouldBe(99);
        r.Strand.ShouldBe("+");
        r.LengthNt.ShouldBe(90);
        r.Partial.ShouldBeFalse();
        r.MeanEntropy.ShouldBe(1.25);
        r.MinEntropy.ShouldBe(0.5);
        r.MaxEntropy.ShouldBe(1.9);
        r.MeanSurprisal.ShouldBeNull();
        r.FractionLowEntropy.ShouldBe(0.1);
    }

    [Fact]
    public void A_header_only_file_is_an_empty_table_not_a_failure()
        => GeneTableReader.Read(Write("a.genes.tsv", Header))!.ShouldBeEmpty();

    [Fact]
    public void A_file_with_the_wrong_header_is_unreadable()
        => GeneTableReader.Read(Write("a.genes.tsv", "position\tbase", "1\tA"))!.ShouldBeNull();

    [Fact]
    public void A_row_that_does_not_parse_is_unreadable_not_silently_dropped()
        => GeneTableReader.Read(Write("a.genes.tsv", Header, "c1\tg\tx\t99\t+\t90\tfalse\tfalse\t1..9\t1\t1\t1\t\t0"))!.ShouldBeNull();

    [Fact]
    public void The_source_finds_every_table_one_folder_down_and_names_it_by_relative_path()
    {
        Write("in1/in1.genes.tsv", Header, "c1\tg1\t1\t9\t+\t9\tfalse\tfalse\t1..9\t1\t1\t1\t\t0");
        Write("in2/in2.genes.tsv", Header);
        Write("in1/in1.entropy.tsv", "position\tbase\tentropy_bits");

        var tables = new GeneTableSource().Load(_dir);

        tables.Select(t => t.RelativePath).ShouldBe([Path.Combine("in1", "in1.genes.tsv"), Path.Combine("in2", "in2.genes.tsv")]);
        tables[0].Rows!.Count.ShouldBe(1);
    }

    [Fact]
    public void A_missing_folder_has_no_tables()
        => new GeneTableSource().Load(Path.Combine(_dir, "nope")).ShouldBeEmpty();
}
