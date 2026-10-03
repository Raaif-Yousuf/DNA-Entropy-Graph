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
}
