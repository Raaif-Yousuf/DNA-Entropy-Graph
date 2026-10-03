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
}
