using DnaEntropyGraph.Core.Contract;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #498: the app's reads of <c>status.json</c> and <c>progress.jsonl</c> (docs/job_contract.md section 4).</summary>
public class WorkerStatusReaderTests
{
    private const string WorkerStatus =
        """{"schema":1,"jobId":"j","stage":"running","heartbeatSeq":118,"updatedAt":"2026-09-18T15:20:03Z","vm":{"gpu":"NVIDIA L4"},"worker":{"version":"1.0.0","image":"sha256:x"},"error":null}""";

    private const string StartupStatus =
        """{"schema":1,"jobId":"j","stage":"installing","updatedAt":"2026-09-18T15:20:03Z","heartbeatSeq":0,"vm":{"name":"n","zone":"z"},"worker":{"version":"","image":""},"percent":0,"detail":{},"error":null}""";

    [Fact]
    public void A_status_the_worker_wrote_is_worker_owned_and_carries_its_heartbeat()
    {
        var status = WorkerStatusReader.TryParseStatus(WorkerStatus);

        status.ShouldNotBeNull();
        status.WorkerOwned.ShouldBeTrue();
        status.HeartbeatSeq.ShouldBe(118);
        status.UpdatedAt.ShouldBe("2026-09-18T15:20:03Z");
        status.Stage.ShouldBe("running");
    }

    [Fact]
    public void A_status_the_startup_script_wrote_is_not_worker_owned()
    {
        var status = WorkerStatusReader.TryParseStatus(StartupStatus);

        status.ShouldNotBeNull();
        status.WorkerOwned.ShouldBeFalse("startup.sh writes an empty worker.version; only the container's worker fills it");
        status.Stage.ShouldBe("installing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"heartbeatSeq\":\"seven\",\"worker\":5}")]
    public void Anything_that_is_not_a_status_object_reads_as_unowned_or_null_and_never_throws(string text)
    {
        var status = WorkerStatusReader.TryParseStatus(text);

        (status is null || !status.WorkerOwned).ShouldBeTrue();
    }

    [Fact]
    public void The_first_progress_line_naming_a_GPU_reads_as_present()
    {
        var jsonl = """{"seq":1,"ts":"t","stage":"restoring-cache","level":"notice","percent":0,"message":"worker starting; GPU: NVIDIA L4 (driver 580.82.07); free disk 90 GB","data":{}}""" + "\n";

        WorkerStatusReader.ReadGpuReport(jsonl).ShouldBe(WorkerGpuReport.Present);
    }

    [Fact]
    public void The_first_progress_line_saying_no_GPU_reads_as_absent()
    {
        var jsonl = """{"seq":1,"ts":"t","stage":"restoring-cache","level":"notice","percent":0,"message":"worker starting; GPU: no GPU detected; free disk 90 GB","data":{}}""" + "\n"
                    + """{"seq":2,"ts":"t","stage":"running","level":"info","percent":1,"message":"later","data":{}}""" + "\n";

        WorkerStatusReader.ReadGpuReport(jsonl).ShouldBe(WorkerGpuReport.Absent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\n")]
    [InlineData("{\"message\":\"something else entirely\"}\n")]
    public void Without_the_worker_starting_line_the_report_is_unknown(string jsonl)
        => WorkerStatusReader.ReadGpuReport(jsonl).ShouldBe(WorkerGpuReport.Unknown);
}
