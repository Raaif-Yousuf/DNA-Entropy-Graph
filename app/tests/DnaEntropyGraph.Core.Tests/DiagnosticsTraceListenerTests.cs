using System.Diagnostics;
using DnaEntropyGraph.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #923: Trace.Trace* output reaches logs\app.log (the folder the diagnostics zip reads), with every line redacted.
/// Trace listeners are process-global, so a parallel test class can hit a real Trace call site and that line lands in this log too:
/// every assertion here looks only at the lines carrying this test's own marker, never at the line count or the whole file.
/// </summary>
public sealed class DiagnosticsTraceListenerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-trace-" + Guid.NewGuid().ToString("N"));

    /// <summary>Unique per test instance; letters and digits only, so no redaction rule touches it.</summary>
    private readonly string _marker = "mk" + Guid.NewGuid().ToString("N")[..8].Replace('-', 'x');

    public void Dispose()
    {
        DiagnosticsTraceListener.Uninstall();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Log => Path.Combine(_root, "logs", "app.log");

    /// <summary>The lines of the log that carry this test's marker.</summary>
    private List<string> Mine() => ReadLines().Where(l => l.Contains(_marker, StringComparison.Ordinal)).ToList();

    /// <summary>Reads the log while a parallel writer may hold it open: a plain ReadAllLines would throw an IOException on a sharing violation.</summary>
    private List<string> ReadLines()
    {
        if (!File.Exists(Log))
        {
            return [];
        }

        using var stream = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public void A_TraceWarning_after_the_startup_registration_lands_as_one_line_in_logs_app_log_and_in_the_diagnostics_zip_listing()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceWarning($"{_marker} session restore failed: IOException");

        var mine = Mine();
        mine.Count.ShouldBe(1);
        mine[0].ShouldEndWith($" WARN trace {_marker} session restore failed: IOException");
        new FolderDiagnosticsSource(_root).ListFiles().ShouldContain("logs/app.log");
    }

    [Fact]
    public void Levels_and_formatted_arguments_are_applied()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceError(_marker + " bad {0} {1}", "one", 2);
        Trace.TraceInformation(_marker + " note");
        Trace.TraceWarning(_marker + " w");

        var mine = Mine();
        mine.ShouldContain(l => l.EndsWith($" ERROR trace {_marker} bad one 2", StringComparison.Ordinal));
        mine.ShouldContain(l => l.EndsWith($" INFO trace {_marker} note", StringComparison.Ordinal));
        mine.ShouldContain(l => l.EndsWith($" WARN trace {_marker} w", StringComparison.Ordinal));
    }

    [Fact]
    public void Installing_twice_leaves_one_listener_so_a_line_is_written_once()
    {
        DiagnosticsTraceListener.Install(_root);
        DiagnosticsTraceListener.Install(_root);

        Trace.Listeners.OfType<DiagnosticsTraceListener>().Count().ShouldBe(1);
        Trace.TraceWarning(_marker + " only once");
        Mine().Count.ShouldBe(1);
    }

    [Fact]
    public void Write_and_WriteLine_fragments_become_one_line()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.Write(_marker + " part one ");
        Trace.WriteLine("part two");

        Mine().ShouldHaveSingleItem().ShouldEndWith($" INFO trace {_marker} part one part two");
    }

    [Fact]
    public void A_Write_fragment_of_one_thread_is_never_glued_onto_a_line_of_another()
    {
        DiagnosticsTraceListener.Install(_root);
        var other = _marker + "other";

        Trace.Write(_marker + " unfinished ");
        var t = new Thread(() => Trace.WriteLine(other + " whole"));
        t.Start();
        t.Join();
        Trace.WriteLine("done");

        var lines = ReadLines();
        lines.ShouldContain(l => l.EndsWith($" INFO trace {other} whole", StringComparison.Ordinal));
        lines.ShouldContain(l => l.EndsWith($" INFO trace {_marker} unfinished done", StringComparison.Ordinal));
        lines.Where(l => l.Contains(other, StringComparison.Ordinal)).ShouldAllBe(l => !l.Contains("unfinished", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Write_that_never_meets_a_WriteLine_is_emitted_once_it_passes_the_cap()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.Write(_marker + new string('x', DiagnosticsTraceListener.MaxPendingChars));

        Mine().ShouldHaveSingleItem().Length.ShouldBeGreaterThan(DiagnosticsTraceListener.MaxPendingChars);
        Trace.WriteLine(_marker + " after");
        Mine().Count.ShouldBe(2);
    }

    [Fact]
    public void A_nucleotide_run_that_spans_the_cap_is_never_written_as_two_raw_halves()
    {
        DiagnosticsTraceListener.Install(_root);

        // Exactly the cap in the first fragment, so the second one is a 10-base tail: flushed on its own it would be a raw run too short to redact.
        var head = _marker + " ";
        Trace.Write(head + new string('A', DiagnosticsTraceListener.MaxPendingChars - head.Length));
        Trace.Write("ACGTACGTAC");
        Trace.WriteLine(string.Empty);
        var text = string.Join("\n", ReadLines());
        text.ShouldNotContain("AAAAAAAAAAAAAAAAAAAA");
        text.ShouldNotContain("ACGTACGTAC");
        text.ShouldContain("<sequence>");
    }

    [Fact]
    public void An_empty_or_blank_message_writes_no_line_and_a_pending_fragment_does_not_survive_a_reinstall()
    {
        DiagnosticsTraceListener.Install(_root);
        var before = ReadLines().Count;

        Trace.TraceWarning("   ");
        Trace.WriteLine(string.Empty);
        Trace.Write(_marker + "stale fragment");
        DiagnosticsTraceListener.Install(_root);
        Trace.WriteLine(_marker + "fresh");

        ReadLines().Count(l => !l.Contains(_marker, StringComparison.Ordinal)).ShouldBe(before, "blank messages add no line (other lines are not mine)");
        Mine().ShouldHaveSingleItem().ShouldEndWith($" INFO trace {_marker}fresh");
    }

    [Fact]
    public void A_message_with_line_breaks_stays_one_line()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceWarning(_marker + " a\r\nb\u2028c\u0000d");

        Mine().ShouldHaveSingleItem().ShouldEndWith($" WARN trace {_marker} a  b c d");
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_throw_into_the_caller()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "deg-trace-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");
        try
        {
            DiagnosticsTraceListener.Install(blocker);

            Should.NotThrow(() => Trace.TraceError(_marker));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Theory]
    [InlineData(@"saved to C:\Users\Alice\Downloads\run1\out.gb failed", "saved to <path>")]
    [InlineData("opened C:/Users/Alice/x.fasta now", "opened <path>")]
    [InlineData(@"share \\lab-server\data\secret.gb gone", "share <path>")]
    [InlineData("contact alice.smith+x@lab.vanderbilt.edu please", "contact <email> please")]
    public void Paths_and_emails_are_replaced(string raw, string expectedStart)
    {
        DiagnosticsTraceListener.Redact(raw).ShouldStartWith(expectedStart);
        DiagnosticsTraceListener.Redact(raw).ShouldNotContain("Alice");
    }

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

    [Theory]
    [InlineData("session restore failed: IOException")]
    [InlineData("job 20261029-120000-abcdef failed: InvalidOperationException")]
    [InlineData("a short ACGT motif is not a sequence and is kept in a long sentence")]
    [InlineData("DialogService: the window has no XamlRoot yet; answering no.")]
    public void Type_names_job_ids_and_short_runs_survive_untouched(string text)
        => DiagnosticsTraceListener.Redact(text).ShouldBe(text);

    [Fact]
    public void A_path_in_a_trace_line_is_redacted_in_the_file_too()
    {
        DiagnosticsTraceListener.Install(_root);

        Trace.TraceWarning(_marker + @" failed C:\Users\Alice\My Documents\a.gb: x");

        var mine = Mine().ShouldHaveSingleItem();
        mine.ShouldContain("<path>");
        mine.ShouldNotContain("Alice");
    }

    [Fact]
    public void After_a_roll_both_the_current_and_the_previous_log_are_in_the_diagnostics_zip()
    {
        var file = new AppLogFile(_root, maxBytes: 300);
        for (var i = 0; i < 12; i++)
        {
            file.AppendLine("2026-10-03T12:00:00.000Z WARN trace filler line number " + i);
        }

        File.Exists(Path.Combine(_root, "logs", "app.1.log")).ShouldBeTrue("a roll happened");
        var info = new DiagnosticsInfo("0.1.0", "Windows", ".NET 10", null, @"C:\Users\someone", [], DateTimeOffset.UnixEpoch, "readme");

        var zip = DiagnosticsBundleBuilder.Build(new FolderDiagnosticsSource(_root), [], info);

        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        // The bundle renames every log (log-01.log...), so assert on the contents: the newest filler line made it, and there are two files.
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
