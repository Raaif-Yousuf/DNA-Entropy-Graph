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
    public void Paths_emails_and_long_nucleotide_runs_are_replaced(string raw, string expectedStart)
    {
        DiagnosticsTraceListener.Redact(raw).ShouldStartWith(expectedStart);
        DiagnosticsTraceListener.Redact(raw).ShouldNotContain("Alice");
        DiagnosticsTraceListener.Redact(raw).ShouldNotContain("ACGTACGT");
    }

    [Theory]
    [InlineData("session restore failed: IOException")]
    [InlineData("job 20261029-120000-abcdef failed: InvalidOperationException")]
    [InlineData("a short ACGT motif is not a sequence and is kept in a long sentence")]
    [InlineData("DialogService: the window has no XamlRoot yet; answering no.")]
    public void Type_names_job_ids_and_short_runs_survive_untouched(string text)
        => DiagnosticsTraceListener.Redact(text).ShouldBe(text);

    [Theory]
    [InlineData("failed while reading ACGTACGTACGTACGTACGTACGT from the input and then stopped")]
    [InlineData("failed while reading acgtnacgtnacgtnacgtnryacgt from the input and then stopped")]
    public void A_long_nucleotide_run_never_reaches_the_log(string raw)
    {
        var redacted = DiagnosticsTraceListener.Redact(raw);

        redacted.ShouldContain("<sequence>");
        redacted.ShouldNotContain("ACGTACGT", Case.Insensitive);
        redacted.ShouldNotContain("nacgtn", Case.Insensitive);
    }

    [Fact]
    public void A_path_in_a_trace_line_is_redacted_in_the_file_too()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceWarning(@"failed C:\Users\Alice\My Documents\a.gb: x");

        var text = ReadLog();
        text.ShouldContain("<path>");
        text.ShouldNotContain("Alice");
    }

    [Fact]
    public void After_a_roll_both_the_current_and_the_previous_log_are_in_the_diagnostics_zip()
    {
        DiagnosticsTraceListener.Install(_root, null);
        var file = new AppLogFile(_root, maxBytes: 300);
        for (var i = 0; i < 12; i++)
        {
            file.AppendLine("2026-10-03T12:00:00.000Z WARN trace filler line number " + i);
        }

        File.Exists(Path.Combine(_root, "logs", "app.1.log")).ShouldBeTrue("a roll happened");
        var info = new DiagnosticsInfo("0.1.0", "Windows", ".NET 10", null, @"C:\Users\someone", [], DateTimeOffset.UnixEpoch, "readme");

        var zip = DiagnosticsBundleBuilder.Build(new FolderDiagnosticsSource(_root), [], info);

        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        // The bundle renames every log (log-01.log...), so assert on the contents: the oldest and the newest filler line both made it.
        var logs = archive.Entries.Where(e => e.FullName.StartsWith("files/logs/", StringComparison.Ordinal)).ToList();
        logs.Count.ShouldBe(2, "the current log and the rolled one");
        logs.ShouldAllBe(e => e.Length > 0);
        string.Concat(logs.Select(e => new StreamReader(e.Open()).ReadToEnd())).ShouldContain("number 11");
    }

    [Fact]
    public void The_listener_uses_the_diagnostics_redactors_rules_so_the_two_cannot_drift()
    {
        // Rules only DiagnosticsRedactor has: a token shape and a sequence file name.
        DiagnosticsTraceListener.Redact("auth ya29.a0AfH6SMBx1234567890abcdef failed").ShouldNotContain("ya29");
        DiagnosticsTraceListener.Redact("read my sample.fasta today").ShouldNotContain("sample.fasta");
    }
}