using DnaEntropyGraph.Presentation.Viewer;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #699: the drag state machine behind the overview strip, tested without a pointer or a window.</summary>
public sealed class OverviewDragTests
{
    [Fact]
    public void A_drag_past_the_click_distance_completes_with_both_ends()
    {
        var drag = new OverviewDrag();
        drag.Begin(100, 40);

        drag.IsActive.ShouldBeTrue();
        drag.Release(500, 90).ShouldBe(new OverviewRange(100, 500));
        drag.IsActive.ShouldBeFalse();
    }

    [Theory]
    [InlineData(40.0, 40.0)]
    [InlineData(40.0, 43.9)]
    [InlineData(43.9, 40.0)]
    public void A_press_and_release_within_four_pixels_is_a_click_not_a_drag(double pressPx, double releasePx)
    {
        var drag = new OverviewDrag();
        drag.Begin(100, pressPx);

        drag.Release(100000, releasePx).ShouldBeNull();
    }

    [Fact]
    public void Four_pixels_is_already_a_drag()
    {
        var drag = new OverviewDrag();
        drag.Begin(100, 40);

        drag.Release(140, 44).ShouldBe(new OverviewRange(100, 140));
    }

    [Fact]
    public void A_release_with_no_press_is_nothing()
    {
        new OverviewDrag().Release(5, 5).ShouldBeNull();
    }

    [Fact]
    public void A_cancelled_drag_does_not_complete()
    {
        var drag = new OverviewDrag();
        drag.Begin(100, 40);
        drag.Cancel();

        drag.IsActive.ShouldBeFalse();
        drag.Release(500, 90).ShouldBeNull();
    }

    [Fact]
    public void Capture_lost_firing_synchronously_inside_the_release_cannot_eat_the_drag()
    {
        var drag = new OverviewDrag();
        drag.Begin(100, 40);

        // The control takes the range first, then releases pointer capture; WinUI may raise PointerCaptureLost (Cancel) inside that call.
        var range = drag.Release(500, 90);
        drag.Cancel();

        range.ShouldBe(new OverviewRange(100, 500));
    }

    [Fact]
    public void A_second_press_restarts_the_drag()
    {
        var drag = new OverviewDrag();
        drag.Begin(100, 40);
        drag.Begin(300, 80);

        drag.Release(400, 120).ShouldBe(new OverviewRange(300, 400));
    }
}
