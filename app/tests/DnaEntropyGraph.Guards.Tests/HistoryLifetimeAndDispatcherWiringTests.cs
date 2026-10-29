using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #100, wired-to-nothing: two calls that exist only in code-behind, where no unit test can see them. The checks read the METHOD BODY with comments removed, so a call that
/// is commented out, or that sits in a different method, no longer satisfies them.
/// </summary>
public class HistoryLifetimeAndDispatcherWiringTests
{
    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", relative));

    /// <summary>The source with // and /* */ comments removed (none of the files read here holds a comment marker inside a string).</summary>
    internal static string WithoutComments(string code) => Regex.Replace(code, @"//[^\r\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>The body of the first method whose signature matches <paramref name="signature"/>: a braced block, or an expression body up to its semicolon. Comments are removed. Null when there is no such method.</summary>
    internal static string? MethodBody(string code, string signature)
    {
        var source = WithoutComments(code);
        var match = Regex.Match(source, signature);
        if (!match.Success)
        {
            return null;
        }

        var open = source.IndexOfAny(['{', '='], match.Index + match.Length);
        if (open < 0)
        {
            return null;
        }

        if (source[open] == '=')
        {
            var end = source.IndexOf(';', open);
            return end < 0 ? null : source[open..end];
        }

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        return null;
    }

    [Fact]
    public void The_Runs_page_deactivates_its_ViewModel_when_it_is_left_so_it_stops_listening_for_run_ends()
    {
        var body = MethodBody(App(Path.Combine("Views", "RunsPage.xaml.cs")), @"void\s+OnNavigatedFrom\s*\([^)]*\)");

        body.ShouldNotBeNull("RunsPage must override OnNavigatedFrom");
        body.ShouldContain("ViewModel.Deactivate()");
    }

    [Fact]
    public void The_dispatcher_is_resolved_on_the_UI_thread_before_anything_else_that_needs_it()
    {
        var body = MethodBody(App("App.xaml.cs"), @"void\s+OnLaunched\s*\([^)]*\)");

        body.ShouldNotBeNull();
        var dispatcher = body.IndexOf("GetRequiredService<Core.Abstractions.IDispatcher>()", StringComparison.Ordinal);
        var shell = body.IndexOf("GetRequiredService<Presentation.ViewModels.ShellViewModel>()", StringComparison.Ordinal);
        dispatcher.ShouldBeGreaterThan(-1, "DispatcherAdapter captures the queue of the thread that first resolves it; the launch must resolve it explicitly, in code");
        shell.ShouldBeGreaterThan(-1, "vacuity: the launch builds the shell from the first ViewModel");
        dispatcher.ShouldBeLessThan(shell, "before the first ViewModel, which could otherwise pull it in from some other thread");
    }

    [Fact]
    public void The_body_reader_ignores_a_commented_out_call_and_a_call_in_another_method()
    {
        const string code = """
            class P
            {
                void Other() { ViewModel.Deactivate(); }
                protected override void OnNavigatedFrom(NavigationEventArgs e)
                {
                    // ViewModel.Deactivate();
                    /* ViewModel.Deactivate(); */
                }
            }
            """;

        MethodBody(code, @"void\s+OnNavigatedFrom\s*\([^)]*\)").ShouldNotBeNull().ShouldNotContain("ViewModel.Deactivate()");
        MethodBody(code, @"void\s+Other\s*\([^)]*\)").ShouldNotBeNull().ShouldContain("ViewModel.Deactivate()");
        MethodBody(code, @"void\s+Missing\s*\([^)]*\)").ShouldBeNull();
        MethodBody("class P { void M() => ViewModel.Deactivate(); }", @"void\s+M\s*\([^)]*\)").ShouldNotBeNull().ShouldContain("ViewModel.Deactivate()");
    }
}
