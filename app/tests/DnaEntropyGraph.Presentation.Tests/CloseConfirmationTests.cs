using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #113: the decision logic behind the window's Closing handler (AppWindowService only forwards to it). The close is cancelled at once, asked once, and a close
/// the question allowed is not asked again.
/// </summary>
public class CloseConfirmationTests
{
    private sealed class Rig
    {
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Asked { get; private set; }

        public int Closed { get; private set; }

        public bool Throws { get; set; }

        public CloseConfirmation Confirmation { get; }

        public Rig() => Confirmation = new CloseConfirmation(ct =>
        {
            Asked++;
            return Throws ? throw new InvalidOperationException("boom") : Answer.Task;
        });

        public bool Close() => Confirmation.ShouldCancelClose(() => Closed++);
    }

    [Fact]
    public void The_first_close_is_cancelled_and_asks_once()
    {
        var rig = new Rig();

        rig.Close().ShouldBeTrue();

        rig.Asked.ShouldBe(1);
        rig.Closed.ShouldBe(0);
    }

    [Fact]
    public void A_second_close_while_the_question_is_open_is_cancelled_and_does_not_ask_again()
    {
        var rig = new Rig();
        rig.Close();

        rig.Close().ShouldBeTrue();

        rig.Asked.ShouldBe(1);
    }

    [Fact]
    public async Task An_allowed_exit_closes_the_window_once_and_that_close_is_not_asked_again()
    {
        var rig = new Rig();
        rig.Close();
        rig.Answer.SetResult(true);
        await rig.Confirmation.Settled;

        rig.Closed.ShouldBe(1);
        rig.Close().ShouldBeFalse(); // the close the question asked for goes through
        rig.Asked.ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_exit_keeps_the_window_and_the_next_close_asks_again()
    {
        var rig = new Rig();
        rig.Close();
        rig.Answer.SetResult(false);
        await rig.Confirmation.Settled;

        rig.Closed.ShouldBe(0);
        var again = new TaskCompletionSource<bool>();
        rig.Close().ShouldBeTrue();
        rig.Asked.ShouldBe(2);
        again.TrySetResult(true);
    }

    [Fact]
    public async Task A_question_that_throws_never_traps_the_user()
    {
        var rig = new Rig { Throws = true };
        rig.Close().ShouldBeTrue();
        await rig.Confirmation.Settled;

        rig.Closed.ShouldBe(1);
    }
}
