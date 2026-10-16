using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #143: the shell knows which page is really showing, through forward navigation, Back and deep links, not only nav-item clicks.</summary>
public sealed class PageKeyHistoryTests
{
    [Fact]
    public void Pushing_makes_a_key_current_and_going_back_restores_the_one_before()
    {
        var history = new PageKeyHistory();

        history.Push("NewRun");
        history.Push("Results");
        history.Current.ShouldBe("Results");
        history.Pop().ShouldBe("NewRun");
        history.Current.ShouldBe("NewRun");
    }

    [Fact]
    public void Going_back_with_one_entry_or_none_keeps_what_it_has()
    {
        var history = new PageKeyHistory();
        history.Current.ShouldBeNull();
        history.Pop().ShouldBeNull();

        history.Push("NewRun");
        history.Pop().ShouldBe("NewRun");
        history.Current.ShouldBe("NewRun");
    }
}
