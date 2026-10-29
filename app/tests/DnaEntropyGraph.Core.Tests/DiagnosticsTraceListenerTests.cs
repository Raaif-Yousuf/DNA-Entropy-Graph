using System.Diagnostics;
using DnaEntropyGraph.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #923: Trace.Trace* output reaches logs\app.log (the folder the diagnostics zip reads), with every line redacted.</summary>
[Collection("TraceListeners")]
public sealed class DiagnosticsTraceListenerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-trace-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => DiagnosticsTraceListener.Uninstall();

    private string Log => Path.Combine(_root, "logs", "app.log");

    private string ReadLog() => File.Exists(Log) ? File.ReadAllText(Log) : string.Empty;

    [Fact]
    public void A_TraceWarning_after_the_startup_registration_lands_as_one_line_in_logs_app_log_and_in_the_diagnostics_zip_listing()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceWarning("session restore failed: IOException");

        var text = ReadLog();
        text.ShouldContain(" WARN trace session restore failed: IOException\n");
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);
        new FolderDiagnosticsSource(_root).ListFiles().ShouldContain("logs/app.log");
    }

    [Fact]
    public void Levels_and_formatted_arguments_are_applied()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceError("bad {0} {1}", "one", 2);
        Trace.TraceInformation("note");
        Trace.TraceWarning("w");

        var text = ReadLog();
        text.ShouldContain(" ERROR trace bad one 2\n");
        text.ShouldContain(" INFO trace note\n");
        text.ShouldContain(" WARN trace w\n");
    }

    [Fact]
    public void Installing_twice_leaves_one_listener_so_a_line_is_written_once()
    {
        DiagnosticsTraceListener.Install(_root);
        DiagnosticsTraceListener.Install(_root);

        Trace.Listeners.OfType<DiagnosticsTraceListener>().Count().ShouldBe(1);
        Trace.TraceWarning("only once");
        ReadLog().Split("only once").Length.ShouldBe(2);
    }

    [Fact]
    public void Write_and_WriteLine_fragments_become_one_line()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.Write("part one ");
        Trace.WriteLine("part two");

        ReadLog().ShouldContain(" INFO trace part one part two\n");
    }

    [Fact]
    public void A_message_with_line_breaks_stays_one_line()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceWarning("a\r\nb\u2028c\u0000d");

        ReadLog().ShouldContain(" WARN trace a  b c d\n");
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_throw_into_the_caller()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "deg-trace-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");
        DiagnosticsTraceListener.Install(blocker);

        Should.NotThrow(() => Trace.TraceError("x"));
    }

    [Theory]
    [InlineData(@"saved to C:\Users\Alice\Downloads\run1\out.gb failed", "saved to <path>")]
    [InlineData("opened C:/Users/Alice/x.fasta now", "opened <path>")]
    [InlineData(@"share \\lab-server\data\secret.gb gone", "share <path>")]
    [InlineData("contact alice.smith+x@lab.vanderbilt.edu please", "contact <email> please")]
    [InlineData("seq ACGTACGTACGTACGTACGTACGT end", "seq <sequence> end")]
    [InlineData("seq acgtnacgtnacgtnacgtnryacgt end", "seq <sequence> end")]
    public void Paths_emails_and_long_nucleotide_runs_are_replaced(string raw, string expectedStart)
    {
        DiagnosticsTraceListener.Redact(raw).ShouldStartWith(expectedStart);
        DiagnosticsTraceListener.Redact(raw).ShouldNotContain("Alice");
        DiagnosticsTraceListener.Redact(raw).ShouldNotContain("ACGTACGT");
    }

    [Theory]
    [InlineData("session restore failed: IOException")]
    [InlineData("job 20261029-120000-abcdef failed: InvalidOperationException")]
    [InlineData("short ACGT run stays, and so does GATTACA")]
    [InlineData("DialogService: the window has no XamlRoot yet; answering no.")]
    public void Type_names_job_ids_and_short_runs_survive_untouched(string text)
        => DiagnosticsTraceListener.Redact(text).ShouldBe(text);

    [Fact]
    public void A_path_in_a_trace_line_is_redacted_in_the_file_too()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceWarning(@"failed C:\Users\Alice\My Documents\a.gb: x");

        var text = ReadLog();
        text.ShouldContain("<path>");
        text.ShouldNotContain("Alice");
    }
}
