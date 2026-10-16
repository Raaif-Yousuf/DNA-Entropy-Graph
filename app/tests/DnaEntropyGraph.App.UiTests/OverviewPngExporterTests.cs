using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.Core.Tracks;
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

    private static EntropyContig Contig(int? seam) => new("c1", 4000, 16, [.. Enumerable.Range(0, 250).Select(i => 1f + (float)Math.Sin(i / 10.0) * 0.5f)], seam);

    [Fact]
    public void A_contig_is_drawn_to_a_real_png_of_the_documented_size()
    {
        var path = Path.Combine(_dir, "a.png");

        new OverviewPngExporter().Export(Contig(seam: 2000), path).ShouldBeTrue();

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

        exporter.Export(Contig(seam: 2000), with).ShouldBeTrue();
        exporter.Export(Contig(seam: null), without).ShouldBeTrue();

        File.ReadAllBytes(with).ShouldNotBe(File.ReadAllBytes(without));
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_false_not_an_exception()
        => new OverviewPngExporter().Export(Contig(null), Path.Combine(_dir, "missing", "a.png")).ShouldBeFalse();
}
