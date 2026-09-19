using DnaEntropyGraph.App.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// Issue #66's "Live log tail from logs/worker.log", at the local-run path
/// job_contract.md section 1 gives: <c>runs/&lt;jobId&gt;/logs/worker.log</c>
/// under the local root instead of a bucket prefix.
/// </summary>
public class FileLogTailReaderTests
{
    [Fact]
    public void A_job_with_no_log_file_yet_returns_an_empty_list_not_an_error()
    {
        var reader = new FileLogTailReader(Path.Combine(Path.GetTempPath(), $"deg-logtail-{Guid.NewGuid():n}"));

        var lines = reader.ReadLines("no-such-job");

        lines.ShouldBeEmpty();
    }

    [Fact]
    public void A_real_worker_log_is_read_back_line_by_line_from_the_documented_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deg-logtail-{Guid.NewGuid():n}");
        var logDir = Path.Combine(root, "job-1", "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllLines(Path.Combine(logDir, "worker.log"), ["OK: booted", "OK: model loaded"]);
        var reader = new FileLogTailReader(root);

        try
        {
            var lines = reader.ReadLines("job-1");

            lines.ShouldBe(["OK: booted", "OK: model loaded"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
