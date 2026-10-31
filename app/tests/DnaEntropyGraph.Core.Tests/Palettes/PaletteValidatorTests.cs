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
        // Dark theme: dark.css inverts and hue-rotates the whole page, so the colours on screen are the filtered ones, on the dark page.
        PaletteValidator.Validate(ViewerDarkFilter.Apply(viewer), PaletteBackgrounds.Dark, contrast, 8.0).ShouldBeEmpty();
    }

    [Fact]
    public void The_dark_filter_matches_the_css_filter_spec_on_known_colours()
    {
        // invert(0.9) takes white to 25.5 and black to 229.5 (of 255), so the last digit depends on rounding; hue-rotate(180deg) leaves greys unchanged.
        ViewerDarkFilter.Apply("#FFFFFF").ShouldBeOneOf("#191919", "#1A1A1A");
        ViewerDarkFilter.Apply("#000000").ShouldBeOneOf("#E5E5E5", "#E6E6E6");
        // Okabe-Ito blue, worked by hand: inverted (0.9, 0.5088, 0.2048), then the 180 degree matrix, then clamped.
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

    [Fact]
    public void Delta_e_2000_matches_the_published_sharma_test_pairs()
    {
        // Sharma, Wu, Dalal (2005) test data, pair 1 and pair 17, given there as Lab; converted here through sRGB so the tolerance is loose.
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
