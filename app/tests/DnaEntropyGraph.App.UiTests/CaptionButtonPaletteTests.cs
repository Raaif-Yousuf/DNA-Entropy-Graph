using DnaEntropyGraph.App.Services;
using Microsoft.UI.Xaml;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// #639: the caption buttons (minimise, maximise, close) of an extended title bar are drawn by the system in the SYSTEM
/// theme, so an app theme that differs from it needs explicit colours or the glyphs vanish (MEASURED 2026-10-03: white
/// glyphs on a light bar, app Light on a dark system). The mapping is a pure function so it needs no window or UI thread.
/// </summary>
public class CaptionButtonPaletteTests
{
    [Fact]
    public void Following_the_system_sets_no_colours_so_the_system_draws_them()
    {
        CaptionButtonPalette.For(ElementTheme.Default).ShouldBeNull();
    }

    [Fact]
    public void Light_draws_dark_glyphs_and_Dark_draws_light_glyphs()
    {
        var light = CaptionButtonPalette.For(ElementTheme.Light);
        var dark = CaptionButtonPalette.For(ElementTheme.Dark);

        light.ShouldNotBeNull();
        dark.ShouldNotBeNull();
        light.Foreground.ShouldBe(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        dark.Foreground.ShouldBe(Windows.UI.Color.FromArgb(255, 255, 255, 255));
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public void The_hover_and_pressed_fills_are_translucent_so_the_bar_shows_through(ElementTheme theme)
    {
        var palette = CaptionButtonPalette.For(theme)!;

        palette.HoverBackground.A.ShouldBeInRange((byte)1, (byte)254);
        palette.PressedBackground.A.ShouldBeInRange((byte)1, (byte)254);
    }
}
