using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #108: work that must wait for the shell's first navigation (an Open-with launch that lands while the window is still starting).</summary>
public class ShellReadinessTests
{
    [Fact]
    public void Work_that_arrives_before_the_shell_is_ready_waits_and_then_runs_once_in_order()
    {
        var readiness = new ShellReadiness();
        var order = new List<int>();

        readiness.WhenReady(() => order.Add(1));
        readiness.WhenReady(() => order.Add(2));
        order.ShouldBeEmpty();

        readiness.MarkReady();
        readiness.MarkReady();

        order.ShouldBe([1, 2]);
    }

    [Fact]
    public void Work_that_arrives_after_the_shell_is_ready_runs_at_once()
    {
        var readiness = new ShellReadiness();
        readiness.MarkReady();
        var ran = false;

        readiness.WhenReady(() => ran = true);

        ran.ShouldBeTrue();
        readiness.IsReady.ShouldBeTrue();
    }

    [Fact]
    public void One_piece_of_work_that_throws_does_not_stop_the_next()
    {
        var readiness = new ShellReadiness();
        var ran = false;
        readiness.WhenReady(() => throw new InvalidOperationException("boom"));
        readiness.WhenReady(() => ran = true);

        readiness.MarkReady();

        ran.ShouldBeTrue();
    }
}
