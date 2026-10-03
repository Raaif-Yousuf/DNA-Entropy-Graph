using System.Text.Json;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #460: FakeGcp's bucket is a real in-memory object store, and its
/// scripted worker writes result.json the way worker/ does. A fake that
/// accepted uploads and forgot them is what let the empty-transfer stub pass.
/// </summary>
public class FakeGcpObjectStoreTests
{
    private const string Bucket = "deg-p-fake";

    [Fact]
    public async Task An_uploaded_object_can_be_downloaded_byte_for_byte()
    {
        var gcp = new FakeGcp();
        using var content = new MemoryStream([1, 2, 3, 4]);

        await gcp.UploadAsync(Bucket, "jobs/j/input/a.gb", content, CancellationToken.None);

        gcp.GetObjectBytes(Bucket, "jobs/j/input/a.gb").ShouldBe([1, 2, 3, 4]);
        await using var back = await gcp.DownloadAsync(Bucket, "jobs/j/input/a.gb", CancellationToken.None);
        using var copy = new MemoryStream();
        await back.CopyToAsync(copy, CancellationToken.None);
        copy.ToArray().ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Upload_reads_from_the_streams_current_position_to_the_end_like_a_retry_replay_would()
    {
        var gcp = new FakeGcp();
        using var content = new MemoryStream([9, 9, 1, 2]);
        content.Position = 2;

        await gcp.UploadAsync(Bucket, "k", content, CancellationToken.None);

        gcp.GetObjectBytes(Bucket, "k").ShouldBe([1, 2]);
    }

    [Fact]
    public async Task A_missing_object_is_null_from_TryDownload_and_a_not_found_error_from_Download()
    {
        var gcp = new FakeGcp();

        (await gcp.TryDownloadAsync(Bucket, "nope", CancellationToken.None)).ShouldBeNull();
        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.DownloadAsync(Bucket, "nope", CancellationToken.None));
        ex.Error.HttpStatus.ShouldBe(404);
    }

    [Fact]
    public async Task Objects_are_per_bucket()
    {
        var gcp = new FakeGcp();
        using var content = new MemoryStream([1]);
        await gcp.UploadAsync("bucket-a", "k", content, CancellationToken.None);

        (await gcp.TryDownloadAsync("bucket-b", "k", CancellationToken.None)).ShouldBeNull();
        gcp.ObjectKeys("bucket-a").ShouldBe(["k"]);
        gcp.ObjectKeys("bucket-b").ShouldBeEmpty();
    }

    private static async Task<FakeGcp> WithManifestAsync(FakeWorkerMode mode, params string[] names)
    {
        var gcp = new FakeGcp().WithWorker(mode);
        var inputs = string.Join(",", names.Select((n, i) => $$"""{"id":"in{{i + 1}}","path":"input/{{n}}.gb","name":"{{n}}"}"""));
        gcp.PutObject(Bucket, "jobs/j1/manifest.json", System.Text.Encoding.UTF8.GetBytes($$"""{"schema":1,"jobId":"j1","inputs":[{{inputs}}]}"""));
        var spec = new VmSpec("p", "i", "j1", "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(1), "DELETE");
        await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);
        return gcp;
    }

    [Fact]
    public async Task The_done_worker_writes_one_track_per_input_and_a_result_listing_it_with_real_checksums()
    {
        var gcp = await WithManifestAsync(FakeWorkerMode.Done, "a", "b");

        var track = gcp.GetObjectBytes(Bucket, "jobs/j1/output/a/a.bedgraph");
        track.ShouldBe(FakeGcp.TrackBytes("a"));
        using var result = JsonDocument.Parse(gcp.GetObjectBytes(Bucket, "jobs/j1/result.json"));
        result.RootElement.GetProperty("status").GetString().ShouldBe("done");
        var file = result.RootElement.GetProperty("inputs")[0].GetProperty("files")[0];
        file.GetProperty("path").GetString().ShouldBe("output/a/a.bedgraph");
        file.GetProperty("bytes").GetInt64().ShouldBe(track.Length);
        file.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(track)).ToLowerInvariant());
    }

    [Fact]
    public async Task The_never_worker_writes_no_result()
    {
        var gcp = await WithManifestAsync(FakeWorkerMode.Never, "a");

        gcp.ObjectKeys(Bucket).ShouldNotContain("jobs/j1/result.json");
    }

    [Fact]
    public async Task A_VM_created_before_any_manifest_exists_gets_no_result_because_there_is_nothing_to_run()
    {
        var gcp = new FakeGcp();
        var spec = new VmSpec("p", "i", "j1", "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(1), "DELETE");

        await gcp.CreateVmAsync(spec, "us-central1-a", CancellationToken.None);

        gcp.ObjectKeys(Bucket).ShouldBeEmpty();
    }
}
