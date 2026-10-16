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
