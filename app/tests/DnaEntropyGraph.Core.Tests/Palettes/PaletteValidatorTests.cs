using DnaEntropyGraph.Core.Palettes;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Palettes;

/// <summary>Issue #195: every shipped palette is checked against the light and dark backgrounds and, pairwise, under three colour-vision simulations.</summary>
public class PaletteValidatorTests
{
    // Default: WCAG 1.4.11 graphics contrast (3:1) and a CIEDE2000 distance of 10 between any two roles under every simulation.
    // High contrast: 7:1 (AAA text level) and 8 (it trades some hue separation for stronger lightness).
    private static (double Contrast, double DeltaE) Bar(string id) => id == TrackPalettes.HighContrastId ? (7.0, 8.0) : (3.0, 10.0);

    public static TheoryData<string, bool> EveryPaletteAndTheme()
    {
        var data = new TheoryData<string, bool>();
        foreach (var palette in TrackPalettes.All)
        {
            data.Add(palette.Id, false);
            data.Add(palette.Id, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPaletteAndTheme))]
    public void A_shipped_palette_passes_the_contrast_and_colour_vision_bar(string id, bool dark)
    {
        var palette = TrackPalettes.All.Single(p => p.Id == id);
        var (contrast, deltaE) = Bar(id);

        PaletteValidator.Validate(palette.For(dark), dark ? PaletteBackgrounds.Dark : PaletteBackgrounds.Light, contrast, deltaE).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TrackPalettes.DefaultId)]
    [InlineData(TrackPalettes.HighContrastId)]
    public void What_the_viewer_gets_passes_both_directly_and_through_the_dark_css_filter(string id)
    {
        var palette = TrackPalettes.All.Single(p => p.Id == id);
        var contrast = id == TrackPalettes.HighContrastId ? 7.0 : 3.0;
        var viewer = palette.ForViewer();

        // Light theme: as given, on white.
        PaletteValidator.Validate(viewer, PaletteBackgrounds.Light, contrast, id == TrackPalettes.HighContrastId ? 8.0 : 10.0).ShouldBeEmpty();
        // Dark theme: dark.css puts the filter on #igv, whose canvases are white, so the tracks sit on the FILTERED white (about #191919),
        // not on the body's #1F1F1F. The colours on screen are the filtered ones.
        var onScreenBackground = ViewerDarkFilter.Apply(PaletteBackgrounds.Light);
        PaletteValidator.Validate(ViewerDarkFilter.Apply(viewer), onScreenBackground, contrast, 8.0).ShouldBeEmpty();    }

    [Fact]
    public void The_dark_filter_matches_the_css_filter_spec_on_known_colours()
    {
        // invert(0.9) takes white to 25.5 and black to 229.5 (of 255), so the last digit depends on rounding; hue-rotate(180deg) leaves greys unchanged.
        ViewerDarkFilter.Apply("#FFFFFF").ShouldBeOneOf("#191919", "#1A1A1A");
        ViewerDarkFilter.Apply("#000000").ShouldBeOneOf("#E5E5E5", "#E6E6E6");
        // Okabe-Ito blue, worked by hand: inverted (0.9, 0.5424, 0.3416), then the 180 degree matrix, then clamped.
        ViewerDarkFilter.Apply("#0072B2").ShouldBe("#4FAADD");
    }

    [Fact]
    public void Two_near_identical_roles_are_reported_under_every_simulation_that_cannot_tell_them_apart()
    {
        var swapped = TrackPalettes.Default.Light with { Reverse = "#0073B3" }; // Entropy is #0072B2

        var problems = PaletteValidator.Validate(swapped, PaletteBackgrounds.Light, 3.0, 10.0);

        problems.ShouldContain(p => p.Contains("Entropy") && p.Contains("Reverse"));
    }

    [Fact]
    public void A_pair_that_differs_only_by_red_versus_green_is_caught_by_the_deuteranopia_simulation()
    {
        // Pure red and a green of the same lightness: far apart for typical vision, close under deuteranopia/protanopia.
        var colours = TrackPalettes.Default.Light with { Forward = "#FF0000", Reverse = "#007F00" };

        var problems = PaletteValidator.Validate(colours, PaletteBackgrounds.Light, 3.0, 10.0);

        problems.ShouldContain(p => p.Contains("Forward") && p.Contains("Reverse") && (p.Contains("Deuteranopia") || p.Contains("Protanopia")));
    }

    [Fact]
    public void A_colour_too_close_to_the_background_is_reported()
    {
        var faint = TrackPalettes.Default.Light with { Smoothed = "#F0F0F0" };

        PaletteValidator.Validate(faint, PaletteBackgrounds.Light, 3.0, 10.0).ShouldContain(p => p.Contains("Smoothed") && p.Contains("background"));
    }

    [Fact]
    public void Contrast_ratio_matches_the_wcag_reference_values()
    {
        PaletteValidator.ContrastRatio("#000000", "#FFFFFF").ShouldBe(21.0, 0.001);
        PaletteValidator.ContrastRatio("#FFFFFF", "#FFFFFF").ShouldBe(1.0, 0.001);
        PaletteValidator.ContrastRatio("#767676", "#FFFFFF").ShouldBe(4.54, 0.01); // the well-known smallest grey with 4.5:1 on white
    }

    // Sharma, Wu and Dalal (2005), "The CIEDE2000 Color-Difference Formula: Implementation Notes", test data given as Lab pairs:
    // pairs 1 to 3 (hue near 270, the dTheta term), 7 (a near-neutral pair), 14 to 17 (hue near 0, where the mean hue wraps),
    // 22, 24, 27 and 31 (the lightness and chroma weighting ends of the table).
    [Theory]
    [InlineData(50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425)]
    [InlineData(50.0000, 3.1571, -77.2803, 50.0000, 0.0000, -82.7485, 2.8615)]
    [InlineData(50.0000, 2.8361, -74.0200, 50.0000, 0.0000, -82.7485, 3.4412)]
    [InlineData(50.0000, 0.0000, 0.0000, 50.0000, -1.0000, 2.0000, 2.3669)]
    [InlineData(50.0000, 2.5000, 0.0000, 73.0000, 25.0000, -18.0000, 27.1492)]
    [InlineData(50.0000, 2.5000, 0.0000, 61.0000, -5.0000, 29.0000, 22.8977)]
    [InlineData(50.0000, 2.5000, 0.0000, 56.0000, -27.0000, -3.0000, 31.9030)]
    [InlineData(50.0000, 2.5000, 0.0000, 58.0000, 24.0000, 15.0000, 19.4535)]
    [InlineData(60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644)]
    [InlineData(61.2901, 3.7196, -5.3901, 61.4292, 2.2480, -4.9620, 1.8731)]
    [InlineData(36.4612, 47.8580, 18.3852, 36.2715, 50.5065, 21.2231, 1.4146)]
    [InlineData(2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082)]
    public void Delta_e_2000_matches_the_published_sharma_test_pairs_to_four_decimals(double l1, double a1, double b1, double l2, double a2, double b2, double expected)
    {
        PaletteValidator.DeltaE2000FromLab((l1, a1, b1), (l2, a2, b2)).ShouldBe(expected, 0.00005);
        PaletteValidator.DeltaE2000FromLab((l2, a2, b2), (l1, a1, b1)).ShouldBe(expected, 0.00005); // the formula is symmetric
    }

    [Fact]
    public void Delta_e_2000_through_srgb_agrees_on_black_and_white()
    {
        PaletteValidator.DeltaE2000("#000000", "#000000", ColorVision.Normal).ShouldBe(0.0, 0.0001);
        PaletteValidator.DeltaE2000("#000000", "#FFFFFF", ColorVision.Normal).ShouldBe(100.0, 0.5);
    }
    [Fact]
    public void Simulation_leaves_greys_alone_and_collapses_red_and_green_under_deuteranopia()
    {
        PaletteValidator.DeltaE2000("#808080", "#808080", ColorVision.Deuteranopia).ShouldBe(0.0, 0.5);
        var normal = PaletteValidator.DeltaE2000("#D55E00", "#009E73", ColorVision.Normal);
        var deut = PaletteValidator.DeltaE2000("#D55E00", "#009E73", ColorVision.Deuteranopia);
        deut.ShouldBeLessThan(normal);
    }
}
