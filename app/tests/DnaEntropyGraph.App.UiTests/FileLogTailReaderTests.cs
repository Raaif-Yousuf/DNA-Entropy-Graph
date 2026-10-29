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

    [Fact]
    public void A_job_id_that_leaves_the_runs_folder_reads_nothing()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"deg-logtail-{Guid.NewGuid():n}");
        var root = Path.Combine(parent, "runs");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(parent, "outside");
        PlantLog(outside);
        var reader = new FileLogTailReader(root);

        try
        {
            reader.ReadLines(Path.Combine("..", "outside")).ShouldBeEmpty();
            reader.ReadLines(outside).ShouldBeEmpty("an absolute job id (C:\\... form) discards the runs folder in Path.Combine");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("x\\..")]
    [InlineData("x\\..\\y")]
    [InlineData("y\\.")]
    public void A_job_id_that_is_not_one_plain_name_reads_no_log_even_when_it_resolves_inside_the_runs_folder(string jobId)
    {
        // "x\..\y" resolves to <runs>\y, another job's log, and "" or "." to <runs>\logs: neither is the named job.
        var root = Path.Combine(Path.GetTempPath(), $"deg-logtail-{Guid.NewGuid():n}");
        PlantLog(Path.Combine(root, "y"));
        PlantLog(root);
        var reader = new FileLogTailReader(root);

        try
        {
            reader.ReadLines(jobId).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PlantLog(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllLines(Path.Combine(folder, "logs", "worker.log"), ["SECRET"]);
    }
    [Theory]
    [InlineData("a\0b")]
    [InlineData("a:stream")]
    public void A_job_id_with_a_nul_or_a_colon_that_names_no_log_returns_empty_and_does_not_throw(string jobId)
    {
        var reader = new FileLogTailReader(Path.Combine(Path.GetTempPath(), $"deg-logtail-{Guid.NewGuid():n}"));

        Should.NotThrow(() => reader.ReadLines(jobId)).ShouldBeEmpty();
    }
}
