using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Palettes;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #195: every track colour is defined once, in <c>Core/Palettes/TrackPalette.cs</c>. The consumers (overview chart, Runs sparkline,
/// PNG export, igv.js load plan and bridge) take it from there; this fails when one of them spells a colour itself, and when one stops reading
/// the palette provider. The viewer's two backgrounds are pinned to <see cref="PaletteBackgrounds"/>, the backgrounds the palettes are validated on.
/// </summary>
public class TrackPaletteSingleSourceGuardTests
{
    private static readonly string[] ConsumerFiles =
    [
        Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropyPlotBuilder.cs"),
        Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropyOverviewChart.cs"),
        Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropySparkline.cs"),
        Path.Combine("src", "DnaEntropyGraph.App", "Services", "OverviewPngExporter.cs"),
        Path.Combine("src", "DnaEntropyGraph.App", "Assets", "viewer", "bridge.js"),
        Path.Combine("src", "DnaEntropyGraph.Presentation", "Viewer", "IgvLoadPlanner.cs"),
        Path.Combine("src", "DnaEntropyGraph.Presentation", "ViewModels", "ViewerViewModel.cs"),
    ];

    // A hex colour, an rgb()/rgba() call, a hand-made Color, or a named ScottPlot colour other than the neutral paper and ink ones.
    private static readonly Regex Literal = new(
        @"#[0-9A-Fa-f]{6}\b|\brgba?\(|Color\.FromArgb|ScottPlot\.Colors\.(?!Transparent\b|White\b|Black\b)\w+|(?<![\w.])Colors\.(?!Transparent\b|White\b|Black\b)\w+",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> Literals(string source)
        => [.. source.Split('\n').Select((line, i) => (line, n: i + 1)).Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && Literal.IsMatch(x.line)).Select(x => $"line {x.n}: {x.line.Trim()}")];

    private static string Read(string relative)
    {
        var path = Path.Combine(RepoPaths.AppRoot, relative);
        File.Exists(path).ShouldBeTrue($"the guard must find {relative}, or it checks nothing.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void No_consumer_spells_a_track_colour()
    {
        foreach (var file in ConsumerFiles)
        {
            Literals(Read(file)).ShouldBeEmpty($"{file} spells a colour itself; take it from TrackPalettes (issue #195).");
        }
    }

    [Theory]
    [InlineData("signal.Color = Color.FromHex(\"#0F6CBD\");")]
    [InlineData("line.Color = ScottPlot.Colors.Orange.WithAlpha(0.25);")]
    [InlineData("return { color: 'rgba(220, 60, 60, 0.6)' };")]
    [InlineData("var c = \"#B54708\";")]
    public void The_scanner_catches_a_planted_colour(string line)
        => Literals(line).Count.ShouldBe(1);

    [Theory]
    [InlineData("plot.FigureBackground.Color = ScottPlot.Colors.White;")]
    [InlineData("var ink = dark ? Colors.White : Colors.Black;")]
    [InlineData("// the old line was #0F6CBD")]
    public void The_scanner_leaves_paper_ink_and_comments_alone(string line)
        => Literals(line).ShouldBeEmpty();

    [Fact]
    public void Every_consumer_reads_the_palette_it_is_meant_to()
    {
        Read(Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropyPlotBuilder.cs")).ShouldContain("PaletteColors");
        Read(Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropyOverviewChart.cs")).ShouldContain("AppPalette.Current");
        Read(Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropySparkline.cs")).ShouldContain("AppPalette.Current");
        Read(Path.Combine("src", "DnaEntropyGraph.App", "Services", "OverviewPngExporter.cs")).ShouldContain("_palettes.Current");
        Read(Path.Combine("src", "DnaEntropyGraph.Presentation", "ViewModels", "ViewerViewModel.cs")).ShouldContain("_palettes?.Current");
        Read(Path.Combine("src", "DnaEntropyGraph.Presentation", "ViewModels", "ViewerViewModel.cs")).ShouldContain(".ForViewer()");
        Read(Path.Combine("src", "DnaEntropyGraph.App", "Startup", "ServiceRegistration.cs")).ShouldContain("ITrackPaletteProvider");
    }

    [Fact]
    public void The_viewer_backgrounds_are_the_ones_the_palettes_are_validated_on()
    {
        Read(Path.Combine("src", "DnaEntropyGraph.App", "Assets", "viewer", "viewer.css")).ShouldContain("background: " + PaletteBackgrounds.Light.ToLowerInvariant());
        var dark = Read(Path.Combine("src", "DnaEntropyGraph.App", "Assets", "viewer", "dark.css"));
        dark.ShouldContain("background: " + PaletteBackgrounds.Dark.ToLowerInvariant());
        // The filter the palette validator simulates (ViewerDarkFilter) is the one the page applies.
        dark.ShouldContain("filter: " + ViewerDarkFilter.CssFilter);
    }
}
