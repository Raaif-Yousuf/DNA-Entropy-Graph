using DnaEntropyGraph.Core.Tracks;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #118: the bedGraph the worker wrote becomes the overview chart's data.</summary>
public sealed class EntropyTrackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-track", Guid.NewGuid().ToString("N"));

    public EntropyTrackTests() => Directory.CreateDirectory(_dir);

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

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n"));
        return path;
    }

    private static string PerBase(string chrom, params double[] values)
        => string.Concat(values.Select((v, i) => FormattableString.Invariant($"{chrom}\t{i}\t{i + 1}\t{v:0.0000}\n")));

    [Fact]
    public void A_per_base_track_becomes_bin_means()
    {
        // 8 bases, bins of 4: means of (1,1,1,1) and (2,2,0,0).
        var path = Write("a.entropy.bedgraph", "track type=bedGraph name=x\n" + PerBase("c1", 1, 1, 1, 1, 2, 2, 0, 0));

        var track = BedGraphReader.Read(path, binSize: 4)!;

        var contig = track.Contigs.ShouldHaveSingleItem();
        contig.Name.ShouldBe("c1");
        contig.Length.ShouldBe(8);
        contig.BinSize.ShouldBe(4);
        contig.Means.ShouldBe([1f, 1f], 0.0001f);
    }

    [Fact]
    public void A_last_partial_bin_is_the_mean_of_the_bases_it_has()
    {
        var path = Write("b.bedgraph", PerBase("c1", 2, 2, 2, 2, 0, 1));

        var contig = BedGraphReader.Read(path, binSize: 4)!.Contigs.Single();

        contig.Length.ShouldBe(6);
        contig.Means.ShouldBe([2f, 0.5f], 0.0001f);
    }

    [Fact]
    public void Two_contigs_stay_separate_and_in_file_order()
    {
        var path = Write("c.bedgraph", PerBase("first", 1, 1) + PerBase("second", 2, 2, 2));

        var track = BedGraphReader.Read(path, binSize: 1)!;

        track.Contigs.Select(c => c.Name).ShouldBe(["first", "second"]);
        track.Contigs[1].Length.ShouldBe(3);
    }

    [Fact]
    public void A_row_that_covers_several_bases_counts_for_each_of_them()
    {
        var path = Write("d.bedgraph", "c1\t0\t4\t1.0\nc1\t4\t8\t2.0\n");

        BedGraphReader.Read(path, binSize: 4)!.Contigs.Single().Means.ShouldBe([1f, 2f], 0.0001f);
    }

    [Fact]
    public void Comment_track_and_browser_lines_and_blank_lines_are_skipped_and_values_read_with_a_dot()
    {
        var path = Write("e.bedgraph", "# note\nbrowser position c1:1-4\n\ntrack type=bedGraph\nc1\t0\t1\t1.5\n");

        BedGraphReader.Read(path, binSize: 1)!.Contigs.Single().Means.ShouldBe([1.5f]);
    }

    [Fact]
    public void A_file_with_no_data_rows_is_null()
        => BedGraphReader.Read(Write("f.bedgraph", "track type=bedGraph\n"), binSize: 1).ShouldBeNull();

    [Fact]
    public void A_malformed_row_makes_the_whole_file_unreadable_rather_than_a_wrong_chart()
        => BedGraphReader.Read(Write("g.bedgraph", "c1\t0\t1\tabc\n"), binSize: 1).ShouldBeNull();

    [Fact]
    public void A_missing_file_is_null()
        => BedGraphReader.Read(Path.Combine(_dir, "nope.bedgraph"), binSize: 1).ShouldBeNull();

    // ---- #700: the reader's speed work must not change what it reads ----

    private string WriteBytes(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));
        return path;
    }

    [Fact]
    public void Windows_line_endings_a_missing_final_newline_and_space_separators_read_the_same()
    {
        var path = WriteBytes("crlf.bedgraph", "# note\r\ntrack type=bedGraph\r\nc1 0 1 1.5\r\nc1\t1\t2\t+2.5e0\r\n  c1\t2\t4\t-0.5  ");
        var means = BedGraphReader.Read(path, binSize: 4)!.Contigs.Single().Means;
        means.ShouldBe([(1.5f + 2.5f - 1f) / 4f], 0.0001f);
    }

    [Fact]
    public void A_row_with_an_extra_field_is_read_and_one_with_too_few_is_unreadable()
    {
        BedGraphReader.Read(WriteBytes("five.bedgraph", "c1\t0\t1\t2\t255,0,0\n"), binSize: 1)!.Contigs.Single().Means.ShouldBe([2f]);
        BedGraphReader.Read(WriteBytes("three.bedgraph", "c1\t0\t1\n"), binSize: 1).ShouldBeNull();
    }

    [Theory]
    [InlineData("c1\t-1\t1\t1\n")]
    [InlineData("c1\t2\t1\t1\n")]
    [InlineData("c1\t0\t1\tNaN\n")]
    [InlineData("c1\t0\t1\tInfinity\n")]
    [InlineData("c1\t0\t1\t1.2.3\n")]
    [InlineData("c1\t0\t99999999999\t1\n")]
    public void Rows_the_old_reader_refused_are_still_refused(string text)
        => BedGraphReader.Read(WriteBytes("bad.bedgraph", text), binSize: 1).ShouldBeNull();

    [Fact]
    public void A_multi_megabyte_file_with_interleaved_contigs_and_wide_rows_matches_a_straightforward_reference()
    {
        // Large enough to straddle any read buffer; mixes per-base rows, wide rows and two contigs that alternate.
        var rnd = new Random(7);
        var text = new System.Text.StringBuilder();
        var position = new[] { 0, 0 };
        for (var row = 0; row < 250_000; row++)
        {
            var c = rnd.Next(2);
            var width = rnd.Next(10) == 0 ? rnd.Next(2, 40) : 1;
            var value = rnd.NextDouble() * 2;
            text.Append(FormattableString.Invariant($"contig{c}\t{position[c]}\t{position[c] + width}\t{value:0.######}\n"));
            position[c] += width;
        }

        var path = WriteBytes("big.bedgraph", text.ToString());
        var got = BedGraphReader.Read(path, binSize: 16)!;
        var want = Reference(text.ToString(), 16);

        got.Contigs.Select(c => c.Name).ShouldBe(want.Keys.ToList());
        foreach (var contig in got.Contigs)
        {
            var (length, means) = want[contig.Name];
            contig.Length.ShouldBe(length);
            contig.Means.Length.ShouldBe(means.Length);
            for (var i = 0; i < means.Length; i++)
            {
                if (float.IsNaN(means[i]))
                {
                    float.IsNaN(contig.Means[i]).ShouldBeTrue();
                }
                else
                {
                    contig.Means[i].ShouldBe(means[i], 0.0001f);
                }
            }
        }
    }

    /// <summary>The slow, obvious reading of a well-formed bedGraph: split each line, add value x bases to each bin.</summary>
    private static Dictionary<string, (int Length, float[] Means)> Reference(string text, int binSize)
    {
        var sums = new Dictionary<string, List<(double Sum, int Count)>>();
        var lengths = new Dictionary<string, int>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            var (start, end, value) = (int.Parse(f[1]), int.Parse(f[2]), double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture));
            if (!sums.TryGetValue(f[0], out var bins))
            {
                sums[f[0]] = bins = [];
                lengths[f[0]] = 0;
            }

            lengths[f[0]] = Math.Max(lengths[f[0]], end);
            for (var p = start; p < end; p++)
            {
                while (bins.Count <= p / binSize)
                {
                    bins.Add((0, 0));
                }

                var (s, n) = bins[p / binSize];
                bins[p / binSize] = (s + value, n + 1);
            }
        }

        return sums.ToDictionary(
            kv => kv.Key,
            kv => (lengths[kv.Key], Enumerable.Range(0, (lengths[kv.Key] + binSize - 1) / binSize).Select(i => i < kv.Value.Count && kv.Value[i].Count > 0 ? (float)(kv.Value[i].Sum / kv.Value[i].Count) : float.NaN).ToArray()));
    }

    [Fact]
    public void Resample_to_fewer_points_averages_neighbours_and_keeps_the_shape()
    {
        TrackResampler.Resample([0f, 2f, 4f, 6f], 2).ShouldBe([1f, 5f], 0.0001f);
    }

    [Fact]
    public void Resample_never_makes_more_points_than_it_was_given()
        => TrackResampler.Resample([1f, 2f], 10).ShouldBe([1f, 2f]);

    [Fact]
    public void Resample_of_nothing_is_nothing()
        => TrackResampler.Resample([], 5).ShouldBeEmpty();

    [Fact]
    public void The_source_picks_the_raw_entropy_track_not_a_smoothed_or_directional_one_and_reads_the_seam_from_provenance()
    {
        Write("run1.entropy.smooth51.bedgraph", PerBase("c1", 9, 9));
        Write("run1.entropy.fwd.bedgraph", PerBase("c1", 8, 8));
        Write("run1.entropy.bedgraph", PerBase("c1", 1, 1, 1, 1));
        Write("provenance.json", """{"contigs":[{"name":"c1","length":4,"seam":2},{"name":"other","seam":null}]}""");

        var overview = new EntropyOverviewSource().Load(_dir)!;

        var contig = overview.Contigs.ShouldHaveSingleItem();
        contig.Means[0].ShouldBe(1f, 0.0001f);
        contig.Seam.ShouldBe(2);
    }

    [Fact]
    public void The_source_finds_a_track_one_folder_down_where_a_real_run_puts_it()
    {
        Write("run1/run1.entropy.bedgraph", PerBase("c1", 1, 1, 1, 1));
        Write("run1/provenance.json", """{"contigs":[{"name":"c1","seam":3}]}""");

        var contig = new EntropyOverviewSource().Load(_dir)!.Contigs.Single();

        contig.Length.ShouldBe(4);
        contig.Seam.ShouldBe(3);
    }

    [Fact]
    public void A_folder_without_a_raw_entropy_track_has_no_overview()
    {
        Write("run1.entropy.smooth51.bedgraph", PerBase("c1", 9, 9));

        new EntropyOverviewSource().Load(_dir).ShouldBeNull();
    }

    [Fact]
    public void A_broken_provenance_file_costs_only_the_seam()
    {
        Write("run1.entropy.bedgraph", PerBase("c1", 1, 1));
        Write("provenance.json", "{ not json");

        new EntropyOverviewSource().Load(_dir)!.Contigs.Single().Seam.ShouldBeNull();
    }
}
