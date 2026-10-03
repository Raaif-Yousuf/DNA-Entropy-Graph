using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #460 x #258: the real transfer goes through the resilience pipeline, so a retried upload must replay the whole file and a retried probe must not read as "missing".</summary>
public class ResilientStorageTransferTests
{
    private const string Project = "my-project";

    private static CloudCallPipeline Pipeline(FakeGcp gcp) => new(
        new CloudRetryOptions { MaxRetryAttempts = 4, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
        gcp,
        new CloudRetryLog());

    /// <summary>Reads part of the first upload then fails it with a 503, the way a dropped connection does mid-body.</summary>
    private sealed class FlakyFirstUpload(FakeGcp inner) : IStorageGateway
    {
        private int _failed;

        public Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken) => inner.EnsureBucketAsync(projectId, cancellationToken);

        public async Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
        {
            if (objectKey.EndsWith("seq.gb", StringComparison.Ordinal) && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                var buffer = new byte[4];
                _ = await content.ReadAsync(buffer, cancellationToken);
                throw new CloudOperationException(new CloudError(null, 503, "connection reset"), CloudErrorKind.Network);
            }

            await inner.UploadAsync(bucket, objectKey, content, cancellationToken);
        }

        public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken) => inner.DownloadAsync(bucket, objectKey, cancellationToken);

        public Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken) => inner.TryDownloadAsync(bucket, objectKey, cancellationToken);
    }

    [Fact]
    public async Task A_run_whose_first_upload_attempt_dies_midway_with_a_503_still_leaves_the_complete_input_in_the_bucket()
    {
        var gcp = new FakeGcp();
        var pipeline = Pipeline(gcp);
        var repo = new InMemoryRunRepository();
        var spec = new VmSpec(Project, "install-1", "job-rt", "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(1), "DELETE");
        var request = TestInputs.Request("job-rt", spec);

        var runner = new CloudJobRunner(
            new ResilientComputeGateway(gcp, pipeline),
            new ResilientStorageGateway(new FlakyFirstUpload(gcp), pipeline),
            new ResilientProjectSetupGateway(gcp, pipeline),
            new ResilientQuotaGateway(gcp, pipeline),
            repo)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(100),
        };

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        gcp.GetObjectBytes(FakeGcp.BucketName(Project), "jobs/job-rt/input/seq.gb").ShouldBe(File.ReadAllBytes(request.Inputs[0].LocalPath));
    }

    [Fact]
    public async Task A_transient_failure_on_the_result_probe_is_retried_and_does_not_read_as_not_there_yet()
    {
        var gcp = new FakeGcp();
        using var content = new MemoryStream([7]);
        await gcp.UploadAsync("b", "k", content, CancellationToken.None);
        var storage = new ResilientStorageGateway(gcp, Pipeline(gcp));
        gcp.WithTransientFailures(503, 1);

        await using var stream = await storage.TryDownloadAsync("b", "k", CancellationToken.None) ?? throw new InvalidOperationException("object reported missing after a retryable failure");

        stream.ReadByte().ShouldBe(7);
    }
}
