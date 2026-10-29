using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issues #107/#108, wired-to-nothing: the calls that exist only in code-behind or the entry point, where no unit test can see them.
/// A source check is crude on purpose: each one is the single line whose absence would leave a tested service never called.
/// </summary>
public class OpenWithWiringTests
{
    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", relative));

    [Fact]
    public void The_window_marks_the_shell_ready_after_its_first_navigation_so_an_open_with_launch_is_not_overwritten()
    {
        var code = App("MainWindow.xaml.cs");

        var select = code.IndexOf("SelectedItem = NavSelection.Find(", StringComparison.Ordinal);
        var ready = code.IndexOf("_readiness.MarkReady()", StringComparison.Ordinal);
        select.ShouldBeGreaterThan(-1);
        ready.ShouldBeGreaterThan(select, "MarkReady must come after the first navigation is selected");

        // Issue #244: a failing or slow restore must still end in MarkReady, or every Open-with launch is dropped.
        var finallyAt = code.IndexOf("finally", StringComparison.Ordinal);
        finallyAt.ShouldBeGreaterThan(select);
        var open = code.IndexOf('{', finallyAt);
        var close = code.IndexOf('}', open);
        ready.ShouldBeInRange(open, close, "MarkReady must sit inside the finally block");
    }

    [Fact]
    public void The_New_run_page_detaches_from_the_intake_when_it_is_left()
        => App(Path.Combine("Views", "NewRunPage.xaml.cs")).ShouldContain("OnNavigatedFrom");

    [Fact]
    public void The_uninstall_hook_removes_the_open_with_entries_and_the_notification_registration()
    {
        var code = App("Program.cs");

        code.ShouldContain("OnBeforeUninstallFastCallback(OnBeforeUninstall)");
        code.ShouldContain("DesktopActivation.UnregisterAll()");
        code.ShouldContain(".Disable()");
    }

    [Fact]
    public void The_app_unregisters_its_notifications_when_the_window_closes()
        => App("App.xaml.cs").ShouldContain("DesktopActivation.Unregister()");
}
