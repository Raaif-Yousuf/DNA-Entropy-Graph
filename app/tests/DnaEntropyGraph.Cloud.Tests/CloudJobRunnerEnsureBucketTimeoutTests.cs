using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #600: <c>EnsureBucketAsync</c> is a sequence of up to ten HTTP requests, each with its own retry backoff inside the
/// gateway, so the runner gives it its own larger budget (<see cref="CloudJobRunner.EnsureBucketTimeout"/>) instead of the
/// 60 s that suits one request. Written from the symptom: a bucket sequence that takes longer than <see cref="CloudJobRunner.CallTimeout"/>
/// but within the bucket budget must complete, never be cut between the insert and the config write.
/// </summary>
public class CloudJobRunnerEnsureBucketTimeoutTests
{
    private static readonly TimeSpan SlowSequence = TimeSpan.FromSeconds(90);

    /// <summary>A storage gateway whose bucket sequence takes <see cref="SlowSequence"/> on the runner's clock.</summary>
    private sealed class SlowBucketStorage(FakeGcp inner, VirtualTimeProvider clock) : IStorageGateway
    {
        public int Completed { get; private set; }

        public async Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken)
        {
            await Task.Yield();
            clock.Advance(SlowSequence);
            cancellationToken.ThrowIfCancellationRequested();
            var name = await inner.EnsureBucketAsync(projectId, cancellationToken);
            Completed++;
            return name;
        }

        public Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken) => inner.UploadAsync(bucket, objectKey, content, cancellationToken);

        public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken) => inner.DownloadAsync(bucket, objectKey, cancellationToken);

        public Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken) => inner.TryDownloadAsync(bucket, objectKey, cancellationToken);

        public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(string projectId, CancellationToken cancellationToken) => inner.ListBucketsAsync(projectId, cancellationToken);
    }

    private static VmSpec Spec(string jobId) => new(
        ProjectId: "my-project",
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static async Task<(CloudJobResult Result, SlowBucketStorage Storage)> RunAsync(TimeSpan bucketBudget)
    {
        var gcp = new FakeGcp();
        var clock = new VirtualTimeProvider();
        var storage = new SlowBucketStorage(gcp, clock);
        var runner = new CloudJobRunner(gcp, storage, gcp, gcp, new InMemoryRunRepository())
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(5),
            CallTimeout = TimeSpan.FromSeconds(60),
            EnsureBucketTimeout = bucketBudget,
            LifecycleTimeout = TimeSpan.FromSeconds(5),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            TimeProvider = clock,
            PollDelay = (span, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(span);
                return Task.CompletedTask;
            },
        };
        var result = await runner.RunAsync(TestInputs.Request("job-600", Spec("job-600")), CancellationToken.None);
        return (result, storage);
    }

    [Fact]
    public async Task A_bucket_sequence_longer_than_the_call_timeout_but_inside_the_bucket_budget_completes_the_run()
    {
        var (result, storage) = await RunAsync(bucketBudget: TimeSpan.FromMinutes(5));

        storage.Completed.ShouldBe(1, "the 90 s bucket sequence was cut by the 60 s one-request deadline");
        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public async Task A_bucket_sequence_past_the_bucket_budget_is_still_cut_as_a_network_failure()
    {
        var (result, storage) = await RunAsync(bucketBudget: TimeSpan.FromSeconds(80));

        storage.Completed.ShouldBe(0);
        result.FinalPhase.ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public void The_default_bucket_budget_is_larger_than_the_one_request_call_timeout()
    {
        var settings = new CloudRunSettings();

        settings.EnsureBucketTimeout.ShouldBe(TimeSpan.FromMinutes(5));
        settings.EnsureBucketTimeout.ShouldBeGreaterThan(settings.CallTimeout);
    }
}


