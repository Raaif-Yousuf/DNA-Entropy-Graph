using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #118: the real ScottPlot and SkiaSharp natives load in this app's output and draw a PNG, with no window or UI thread.</summary>
public sealed class OverviewPngExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-overview-png", Guid.NewGuid().ToString("N"));

    public OverviewPngExporterTests() => Directory.CreateDirectory(_dir);

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

    private static readonly OverviewImageLabels Labels = new("Run one: c1", "Position (bases)", "Entropy (bits)");

    private static EntropyContig Contig(int? seam) => new("c1", 4000, 16, [.. Enumerable.Range(0, 250).Select(i => 1f + (float)Math.Sin(i / 10.0) * 0.5f)], seam);

    [Fact]
    public void A_contig_is_drawn_to_a_real_png_of_the_documented_size()
    {
        var path = Path.Combine(_dir, "a.png");

        new OverviewPngExporter().Export(Contig(seam: 2000), Labels, path).ShouldBeTrue();

        var bytes = File.ReadAllBytes(path);
        bytes[..8].ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        bytes.Length.ShouldBeGreaterThan(2000);
    }

    [Fact]
    public void The_seam_changes_the_picture()
    {
        var with = Path.Combine(_dir, "with.png");
        var without = Path.Combine(_dir, "without.png");
        var exporter = new OverviewPngExporter();

        exporter.Export(Contig(seam: 2000), Labels, with).ShouldBeTrue();
        exporter.Export(Contig(seam: null), Labels, without).ShouldBeTrue();

        File.ReadAllBytes(with).ShouldNotBe(File.ReadAllBytes(without));
    }

    [Fact]
    public void The_title_and_axis_labels_are_drawn_into_the_picture()
    {
        var titled = Path.Combine(_dir, "titled.png");
        var bare = Path.Combine(_dir, "bare.png");
        var exporter = new OverviewPngExporter();

        exporter.Export(Contig(seam: null), Labels, titled).ShouldBeTrue();
        exporter.Export(Contig(seam: null), new OverviewImageLabels(string.Empty, string.Empty, string.Empty), bare).ShouldBeTrue();
        File.ReadAllBytes(titled).ShouldNotBe(File.ReadAllBytes(bare));

        // Each piece of text alone changes the picture, so none of the three can be dropped unnoticed.
        var bytes = new List<byte[]>();
        foreach (var labels in new[] { Labels with { Title = string.Empty }, Labels with { XAxis = string.Empty }, Labels with { YAxis = string.Empty } })
        {
            var path = Path.Combine(_dir, $"part{bytes.Count}.png");
            exporter.Export(Contig(seam: null), labels, path).ShouldBeTrue();
            bytes.Add(File.ReadAllBytes(path));
        }

        foreach (var without in bytes)
        {
            without.ShouldNotBe(File.ReadAllBytes(titled));
        }
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_false_not_an_exception()
        => new OverviewPngExporter().Export(Contig(null), Labels, Path.Combine(_dir, "missing", "a.png")).ShouldBeFalse();
}
