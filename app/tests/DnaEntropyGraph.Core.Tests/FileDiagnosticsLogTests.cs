using System.Text;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

public class FileDiagnosticsLogTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void A_warning_is_one_ascii_LF_line_with_the_job_id_and_the_error_class_under_logs()
    {
        var root = Path.Combine(Path.GetTempPath(), "deg-log-" + Guid.NewGuid().ToString("N"));
        var log = new FileDiagnosticsLog(root, new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)));

        log.Warning("reconciler", "job-1", "ArgumentOutOfRangeException");
        log.Warning("reconcile-on-reconnect", null, "InvalidOperationException");

        var bytes = File.ReadAllBytes(Path.Combine(root, "logs", "app.log"));
        bytes.Any(b => b == (byte)'\r' || b > 127).ShouldBeFalse("LF only, ASCII only");
        Encoding.UTF8.GetString(bytes).ShouldBe(
            "2026-10-03T12:00:00.000Z WARN reconciler job=job-1 error=ArgumentOutOfRangeException\n"
            + "2026-10-03T12:00:00.000Z WARN reconcile-on-reconnect job=- error=InvalidOperationException\n");
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_throw()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "deg-log-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");

        Should.NotThrow(() => new FileDiagnosticsLog(blocker).Warning("reconciler", "job-1", "X"));
    }

    [Fact]
    public void A_log_whose_root_makes_the_framework_throw_something_other_than_an_IO_error_does_not_throw()
    {
        // A null character in the path is an ArgumentException from Directory.CreateDirectory, not an IOException: the log is called from
        // inside the reconciler's own catch blocks, so whatever it throws would end the whole lifecycle pass (issue #575 item 2).
        var log = new FileDiagnosticsLog("C:\\deg-log\0bad");

        Should.NotThrow(() => log.Warning("reconciler", "job-1", "X"));
    }

    [Fact]
    public void The_log_never_grows_past_its_cap_plus_one_line_and_keeps_the_newest_lines_with_one_rotated_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "deg-log-cap-" + Guid.NewGuid().ToString("N"));
        var log = new FileDiagnosticsLog(root, new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)), maxBytes: 400);
        var path = Path.Combine(root, "logs", "app.log");

        for (var i = 0; i < 100; i++)
        {
            log.Warning("reconciler", "job-" + i, "InvalidOperationException");
        }

        new FileInfo(path).Length.ShouldBeLessThanOrEqualTo(400 + 120, "one appended line past the cap at most");
        File.Exists(path + ".1").ShouldBeTrue("the previous file is kept once, not deleted");
        new FileInfo(path + ".1").Length.ShouldBeLessThanOrEqualTo(400 + 120);
        Directory.GetFiles(Path.Combine(root, "logs")).Length.ShouldBe(2, "never more than the log and one rotated copy");
        File.ReadAllText(path).ShouldContain("job=job-99 ", Case.Sensitive, "the newest line is in the live file");
    }

    [Fact]
    public void A_rotation_that_cannot_move_the_file_still_appends_lines_up_to_twice_the_cap_instead_of_dropping_them()
    {
        // The diagnostics zip (or anything) holding app.log.1 open makes the rotating Move throw; a brief overshoot of the cap beats lost lines,
        // but only a brief one (see the next test).
        var root = Path.Combine(Path.GetTempPath(), "deg-log-held-" + Guid.NewGuid().ToString("N"));
        var log = new FileDiagnosticsLog(root, new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)), maxBytes: 400);
        var path = Path.Combine(root, "logs", "app.log");
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllText(path + ".1", "old\n");
        using var held = new FileStream(path + ".1", FileMode.Open, FileAccess.Read, FileShare.None);

        for (var i = 0; i < 8; i++)
        {
            log.Warning("reconciler", "job-" + i, "InvalidOperationException");
        }

        var text = File.ReadAllText(path);
        for (var i = 0; i < 8; i++)
        {
            text.ShouldContain($"job=job-{i} ", Case.Sensitive, "every line is kept while the rotation is blocked and the file is under twice the cap");
        }
    }

    [Fact]
    public void A_rotation_that_fails_for_good_cannot_make_the_log_grow_without_bound()
    {
        // Issue #575 review: app.log.1 is read-only, so the rotating Move fails on every write; the old behaviour appended forever.
        var root = Path.Combine(Path.GetTempPath(), "deg-log-stuck-" + Guid.NewGuid().ToString("N"));
        const long cap = 400;
        var log = new FileDiagnosticsLog(root, new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)), maxBytes: cap);
        var path = Path.Combine(root, "logs", "app.log");
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllText(path + ".1", "old\n");
        File.SetAttributes(path + ".1", FileAttributes.ReadOnly);
        try
        {
            for (var i = 0; i < 200; i++)
            {
                log.Warning("reconciler", "job-" + i, "InvalidOperationException");
            }

            new FileInfo(path).Length.ShouldBeLessThanOrEqualTo(2 * cap + 120, "the live file stops growing at about twice the cap while rotation keeps failing");
        }
        finally
        {
            File.SetAttributes(path + ".1", FileAttributes.Normal);
        }

        log.Warning("reconciler", "job-after", "InvalidOperationException");
        File.ReadAllText(path).ShouldContain("job=job-after ", Case.Sensitive, "once rotation can work again the newest line is kept");
        new FileInfo(path).Length.ShouldBeLessThanOrEqualTo(cap + 120);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u000bb")]
    [InlineData("a\u000cb")]
    [InlineData("a\u0000b")]
    [InlineData("a\u001bb")]
    public void A_source_or_error_class_with_any_line_break_or_control_character_stays_one_line(string hostile)
    {
        var root = Path.Combine(Path.GetTempPath(), "deg-log-clean-" + Guid.NewGuid().ToString("N"));
        var log = new FileDiagnosticsLog(root, new FixedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)));

        log.Warning(hostile, hostile, hostile);

        var text = File.ReadAllText(Path.Combine(root, "logs", "app.log"));
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1, "one event is one line");
        text.EndsWith('\n').ShouldBeTrue();
        text[..^1].Any(c => char.IsControl(c) || c is '\u2028' or '\u2029').ShouldBeFalse("no control or Unicode line separator survives into the log");
        text.Contains("a b", StringComparison.Ordinal).ShouldBeTrue("the character is replaced by a space, so the words stay apart");
    }

    [Fact]
    public void The_hostile_character_theory_is_not_vacuous()
    {
        // Guards the Theory above: it must cover every class of character the cleaner is meant to strip.
        var cases = typeof(FileDiagnosticsLogTests).GetMethod(nameof(A_source_or_error_class_with_any_line_break_or_control_character_stays_one_line))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Length;

        cases.ShouldBeGreaterThanOrEqualTo(9);
    }
}
