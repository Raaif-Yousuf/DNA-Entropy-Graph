using DnaEntropyGraph.Core.Contract;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #460: result.json (docs/job_contract.md section 7) as the app reads it. result.json existing is the worker's terminal signal.</summary>
public class WorkerResultReaderTests
{
    private const string Done = """
    {"schema":1,"jobId":"j1","status":"done","inputs":[
      {"id":"in1","status":"done","outputs":["output/S/S.bedgraph","output/S/S.gb"],
       "files":[{"path":"output/S/S.bedgraph","sha256":"AB12","bytes":10},{"path":"output/S/S.gb","sha256":"cd34","bytes":20}],
       "notices":[],"stats":null,"error":null}],
     "timing":{},"gpu":{},"error":null}
    """;

    [Fact]
    public void Reads_status_inputs_and_files_with_checksums()
    {
        var r = WorkerResultReader.Parse(Done);

        r.Status.ShouldBe("done");
        r.Inputs.Count.ShouldBe(1);
        r.Inputs[0].Id.ShouldBe("in1");
        r.Inputs[0].Status.ShouldBe("done");
        r.Inputs[0].Files.Select(f => f.Path).ShouldBe(["output/S/S.bedgraph", "output/S/S.gb"]);
        r.Inputs[0].Files[0].Sha256.ShouldBe("AB12");
        r.Inputs[0].Files[1].Bytes.ShouldBe(20);
    }

    [Fact]
    public void A_listed_output_with_no_files_entry_is_still_downloadable_without_a_checksum()
    {
        var r = WorkerResultReader.Parse("""{"schema":1,"jobId":"j","status":"done","inputs":[{"id":"in1","status":"done","outputs":["output/a.txt"]}]}""");

        r.Inputs[0].Files.Single().Path.ShouldBe("output/a.txt");
        r.Inputs[0].Files.Single().Sha256.ShouldBeNull();
    }

    [Fact]
    public void A_whole_batch_refusal_reads_the_error_code_and_message()
    {
        var r = WorkerResultReader.Parse("""{"schema":1,"jobId":"j","status":"failed","inputs":[],"error":{"code":"BATCH_LIMIT_EXCEEDED","message":"too big","retriable":false}}""");

        r.Status.ShouldBe("failed");
        r.ErrorCode.ShouldBe("BATCH_LIMIT_EXCEEDED");
        r.ErrorMessage.ShouldBe("too big");
        r.Inputs.ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_input_carries_its_own_error()
    {
        var r = WorkerResultReader.Parse("""{"schema":1,"jobId":"j","status":"done","inputs":[{"id":"in1","status":"failed","outputs":[],"error":{"code":"MODEL_OOM","message":"oom"}}]}""");

        r.Inputs[0].Status.ShouldBe("failed");
        r.Inputs[0].ErrorCode.ShouldBe("MODEL_OOM");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schema":1,"jobId":"j"}""")]
    [InlineData("""{"schema":1,"jobId":"j","status":"banana","inputs":[]}""")]
    [InlineData("""{"schema":2,"jobId":"j","status":"done","inputs":[]}""")]
    public void Garbage_or_unknown_shape_is_rejected_rather_than_read_as_success(string json)
        => Should.Throw<InvalidDataException>(() => WorkerResultReader.Parse(json));

    [Theory]
    [InlineData("""{"schema":1,"jobId":"j","stage":"failed","error":{"code":"GPU_NOT_VISIBLE","retriable":false}}""", "GPU_NOT_VISIBLE")]
    [InlineData("""{"schema":1,"stage":"failed","error":{"code":"IMAGE_PULL_FAILED","retriable":true}}""", "IMAGE_PULL_FAILED")]
    [InlineData("""{"schema":1,"stage":"running","error":null}""", null)]
    [InlineData("""{"schema":1,"stage":"failed"}""", null)]
    [InlineData("""{"error":{"code":7}}""", null)]
    [InlineData("""{"error":"GPU_NOT_VISIBLE"}""", null)]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    [InlineData("", null)]
    public void The_error_code_in_status_json_is_read_leniently_and_only_when_it_is_a_string(string json, string? expected)
        => WorkerResultReader.TryReadStatusErrorCode(json).ShouldBe(expected);

    [Theory]
    [InlineData("GPU_NOT_VISIBLE", "gpu_not_visible")]
    [InlineData("IMAGE_PULL_FAILED", "image_pull_failed")]
    [InlineData("MANIFEST_INVALID", "manifest_invalid")]
    [InlineData("WORKER_CRASH", "worker_crashed")]
    [InlineData("MODEL_OOM", "model_oom")]
    [InlineData("MODEL_NEEDS_HOPPER", "model_needs_hopper")]
    [InlineData("BATCH_LIMIT_EXCEEDED", "batch_limit_exceeded")]
    [InlineData("INPUT_INVALID", "input_invalid")]
    [InlineData("WORKER_VERSION_MISMATCH", "worker_version_mismatch")]
    [InlineData("MODEL_UNKNOWN", null)]
    [InlineData(null, null)]
    public void A_worker_status_code_maps_to_its_own_run_error_code_or_to_nothing(string? workerCode, string? expected)
        => DnaEntropyGraph.Core.Cloud.RunErrorCodes.ForWorkerStatusCode(workerCode).ShouldBe(expected);
}
