using System.Text;
using DnaEntropyGraph.Core.Viewers;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Viewers;

/// <summary>Issue #497: the .fai writer, checked against samtools faidx's documented output (name, length, offset, line bases, line width).</summary>
public sealed class FastaIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-fai-" + Guid.NewGuid().ToString("N"));

    public FastaIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static List<FastaIndexEntry> Build(string text, int bufferSize = 1 << 16)
        => FastaIndexWriter.Build(new MemoryStream(Encoding.ASCII.GetBytes(text)), bufferSize);

    [Fact]
    public void Two_records_match_samtools_faidx()
    {
        var e = Build(">seq1 desc\nACGTACGT\nACGTACGT\nACG\n>seq2\nTTTT\n");

        e.ShouldBe([new FastaIndexEntry("seq1", 19, 11, 8, 9), new FastaIndexEntry("seq2", 4, 39, 4, 5)]);
    }

    [Fact]
    public void Crlf_line_endings_count_both_bytes_in_the_line_width()
    {
        var e = Build(">seq1 desc\r\nACGTACGT\r\nACGTACGT\r\nACG\r\n>seq2\r\nTTTT\r\n");

        e.ShouldBe([new FastaIndexEntry("seq1", 19, 12, 8, 10), new FastaIndexEntry("seq2", 4, 44, 4, 6)]);
    }

    [Fact]
    public void A_last_line_with_no_newline_still_counts()
    {
        Build(">a\nACGT\nAC").ShouldBe([new FastaIndexEntry("a", 6, 3, 4, 5)]);
    }

    [Fact]
    public void A_single_unwrapped_line_with_no_newline_has_equal_bases_and_width()
    {
        Build(">a\nACGTACGT").ShouldBe([new FastaIndexEntry("a", 8, 3, 8, 8)]);
    }

    [Fact]
    public void Trailing_blank_lines_are_ignored()
    {
        Build(">a\nACGT\nAC\n\n\n").ShouldBe([new FastaIndexEntry("a", 6, 3, 4, 5)]);
    }

    [Fact]
    public void A_tiny_read_buffer_gives_the_same_index()
    {
        var text = ">seq1 desc\nACGTACGT\nACGTACGT\nACG\n>seq2\nTTTT\n";
        Build(text, bufferSize: 1).ShouldBe(Build(text));
    }

    [Fact]
    public void A_record_with_no_bases_has_zero_length()
    {
        Build(">empty\n>b\nAC\n").ShouldBe([new FastaIndexEntry("empty", 0, 7, 0, 0), new FastaIndexEntry("b", 2, 10, 2, 3)]);
    }

    [Theory]
    [InlineData("ACGT\n>a\nAC\n")]
    [InlineData(">a\nACGT\nAC\nACGT\n")]
    [InlineData(">a\nAC\nACGT\n")]
    [InlineData(">a\nACGT\n\nACGT\n")]
    [InlineData(">a\nAC\n>a\nAC\n")]
    [InlineData(">a\nACGT\r\nACGT\n")]
    public void A_file_samtools_would_refuse_is_a_FormatException(string text)
    {
        Should.Throw<FormatException>(() => Build(text));
    }

    [Fact]
    public void Write_emits_tab_separated_lines_with_lf()
    {
        var sw = new StringWriter { NewLine = "\n" };
        FastaIndexWriter.Write([new FastaIndexEntry("seq1", 19, 11, 8, 9)], sw);

        sw.ToString().ShouldBe("seq1\t19\t11\t8\t9\n");
    }

    [Fact]
    public void BuildTo_writes_the_fai_utf8_without_bom_and_leaves_no_temp_file()
    {
        var fasta = Path.Combine(_dir, "s.fasta");
        File.WriteAllText(fasta, ">a\nACGT\nAC\n");
        var fai = Path.Combine(_dir, "out", "s.fai");

        FastaIndexWriter.BuildTo(fasta, fai);

        File.ReadAllBytes(fai).ShouldBe(Encoding.ASCII.GetBytes("a\t6\t3\t4\t5\n"));
        Directory.GetFiles(Path.Combine(_dir, "out")).Select(Path.GetFileName).ToArray().ShouldBe(["s.fai"]);
    }

    [Fact]
    public void BuildTo_with_a_bad_fasta_leaves_no_fai()
    {
        var fasta = Path.Combine(_dir, "bad.fasta");
        File.WriteAllText(fasta, "ACGT\n");
        var fai = Path.Combine(_dir, "out", "bad.fai");

        Should.Throw<FormatException>(() => FastaIndexWriter.BuildTo(fasta, fai));

        Directory.GetFiles(Path.Combine(_dir, "out")).ShouldBeEmpty();
    }

    [Fact]
    public void Store_writes_only_under_its_own_folder_and_never_beside_the_fasta()
    {
        var runFolder = Path.Combine(_dir, "run");
        Directory.CreateDirectory(runFolder);
        var fasta = Path.Combine(runFolder, "s.fasta");
        File.WriteAllText(fasta, ">a\nACGT\nAC\n");
        var store = new FastaIndexStore(Path.Combine(_dir, "fai"));

        var fai = store.EnsureIndex(fasta);

        Path.GetDirectoryName(fai).ShouldBe(store.Directory);
        Path.GetExtension(fai).ShouldBe(".fai");
        File.Exists(fai).ShouldBeTrue();
        Directory.GetFiles(runFolder).Select(Path.GetFileName).ToArray().ShouldBe(["s.fasta"]);
    }

    [Fact]
    public void Store_reuses_the_index_while_the_fasta_is_unchanged_and_rebuilds_when_it_changes()
    {
        var fasta = Path.Combine(_dir, "s.fasta");
        File.WriteAllText(fasta, ">a\nACGT\nAC\n");
        var store = new FastaIndexStore(Path.Combine(_dir, "fai"));

        var first = store.EnsureIndex(fasta);
        var stamp = File.GetLastWriteTimeUtc(first);
        File.SetLastWriteTimeUtc(first, stamp.AddHours(-1));
        store.EnsureIndex(fasta).ShouldBe(first);
        File.GetLastWriteTimeUtc(first).ShouldBe(stamp.AddHours(-1));

        File.WriteAllText(fasta, ">a\nACGT\nACGT\nAC\n");
        var second = store.EnsureIndex(fasta);
        second.ShouldNotBe(first);
        File.ReadAllText(second).ShouldBe("a\t10\t3\t4\t5\n");
    }
}
