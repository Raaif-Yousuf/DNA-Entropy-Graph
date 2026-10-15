using DnaEntropyGraph.App.Startup;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #107: an activation that lands before the window exists is kept, in order, and delivered once the UI attaches.</summary>
public sealed class ActivationInboxTests
{
    [Fact]
    public void Activations_posted_before_the_handler_is_attached_are_delivered_in_order_on_attach()
    {
        var inbox = new ActivationInbox();
        inbox.Post("run=a");
        inbox.Post(null);
        inbox.Post("run=b");
        var seen = new List<string?>();

        inbox.Attach(seen.Add);

        seen.ShouldBe(["run=a", null, "run=b"]);
    }

    [Fact]
    public void Activations_after_attach_go_straight_to_the_handler_and_nothing_is_delivered_twice()
    {
        var inbox = new ActivationInbox();
        inbox.Post("run=a");
        var seen = new List<string?>();
        inbox.Attach(seen.Add);

        inbox.Post("run=c");

        seen.ShouldBe(["run=a", "run=c"]);
    }
}
