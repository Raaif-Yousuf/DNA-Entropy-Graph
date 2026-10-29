using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #100 round 3, wired-to-nothing: two calls that exist only in code-behind, where no unit test can see them.</summary>
public class HistoryLifetimeAndDispatcherWiringTests
{
    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", relative));

    [Fact]
    public void The_Runs_page_deactivates_its_ViewModel_when_it_is_left_so_it_stops_listening_for_run_ends()
    {
        var code = App(Path.Combine("Views", "RunsPage.xaml.cs"));

        code.ShouldContain("OnNavigatedFrom");
        code.ShouldContain("ViewModel.Deactivate()");
    }

    [Fact]
    public void The_dispatcher_is_resolved_on_the_UI_thread_before_anything_else_that_needs_it()
    {
        var code = App("App.xaml.cs");
        var launched = code.IndexOf("protected override void OnLaunched", StringComparison.Ordinal);
        launched.ShouldBeGreaterThan(-1);

        var dispatcher = code.IndexOf("IDispatcher", launched, StringComparison.Ordinal);
        var shell = code.IndexOf("ShellViewModel", launched, StringComparison.Ordinal);
        dispatcher.ShouldBeGreaterThan(-1, "DispatcherAdapter captures the queue of the thread that first resolves it; the launch must resolve it explicitly");
        dispatcher.ShouldBeLessThan(shell, "before the first ViewModel, which could otherwise pull it in from some other thread");
    }
}
