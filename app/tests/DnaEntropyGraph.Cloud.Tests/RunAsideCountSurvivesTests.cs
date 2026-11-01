using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #1059 (after #929): the count of the user's changed files a download renamed aside (Hard Rule 14) reaches the run's terminal row on the paths that
/// do not go through <c>RunOutcomeRecorder.AsideNote</c>: a cancel after a rename, a failure that is not a <c>RunFailureException</c> (a project-wide cloud
/// error, a raw exception) after a rename, and a resume after the app closed mid-download. Each test drives the real runner against FakeGcp, with a
/// storage decorator that fails or cancels the second result file, and reads the count back from the row with <see cref="KeptAsideDetail.TryRead"/>.
/// </summary>
public sealed class RunAsideCountSurvivesTests : IDisposable
{
    private const string Project = "my-project";
    private static readonly string Bucket = FakeGcp.BucketName(Project);
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-1059-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryRunRepository _repo = new();

    public RunAsideCountSurvivesTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static VmSpec Spec(string jobId) => new(Project, "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE");

    /// <summary>Passes everything to the fake, except that the second result file ("two") runs <paramref name="onSecondFile"/> first.</summary>
    private sealed class SecondFileHook(FakeGcp inner, Func<Task>? onSecondFile) : IStorageGateway
    {
        public Func<Task>? OnSecondFile { get; set; } = onSecondFile;

        public Task<string> EnsureBucketAsync(CloudScope scope, CancellationToken cancellationToken) => inner.EnsureBucketAsync(scope, cancellationToken);

        public Task UploadAsync(CloudScope scope, string bucket, string objectKey, Stream content, CancellationToken cancellationToken) => inner.UploadAsync(scope, bucket, objectKey, content, cancellationToken);

        public Task<Stream> DownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken) => inner.DownloadAsync(scope, bucket, objectKey, cancellationToken);

        public Task<Stream?> TryDownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken) => inner.TryDownloadAsync(scope, bucket, objectKey, cancellationToken);

        public async Task<bool> TryDownloadToFileAsync(CloudScope scope, string bucket, string objectKey, string destinationPath, CancellationToken cancellationToken)
        {
            if (objectKey.EndsWith("two/two.bedgraph", StringComparison.Ordinal) && OnSecondFile is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            return await inner.TryDownloadToFileAsync(scope, bucket, objectKey, destinationPath, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(CloudScope scope, CancellationToken cancellationToken) => inner.ListBucketsAsync(scope, cancellationToken);

        public Task<PrefixUsage> GetPrefixUsageAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken) => inner.GetPrefixUsageAsync(scope, bucket, prefix, cancellationToken);

        public Task<IReadOnlyList<string>> ListChildPrefixesAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken) => inner.ListChildPrefixesAsync(scope, bucket, prefix, cancellationToken);

        public Task<int> DeletePrefixAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken) => inner.DeletePrefixAsync(scope, bucket, prefix, cancellationToken);
    }

    private static CloudJobRunner Runner(FakeGcp gcp, IStorageGateway storage, IRunRepository repo)
    {
        var clocks = new RunnerClocks();
        return new(gcp, storage, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromMilliseconds(60),
            TimeProvider = clocks.Run,
            PollDelay = clocks.SleepAsync,
        };
    }

    /// <summary>
    /// A two-input request whose run folder is already on disk (marked as the app would have made it) holding an EDITED copy of the first result file,
    /// and a row that names it: what a resumed run finds. The first file is therefore renamed aside when it downloads.
    /// </summary>
    private async Task<(CloudJobRequest Request, string Folder)> SeedEditedFirstFileAsync(string jobId)
    {
        var request = TestInputs.Request(jobId, Spec(jobId), fileNames: ["seq.gb", "two.gb"]);
        var folder = Path.Combine(request.OutputFolder, "resumed");
        Directory.CreateDirectory(Path.Combine(folder, "seq"));
        RunFolderMarker.Write(folder, jobId);
        await File.WriteAllTextAsync(Path.Combine(folder, "seq", "seq.bedgraph"), "the user edited this", TestContext.Current.CancellationToken);
        await _repo.UpsertAsync(new RunRecord(jobId, JobPhase.Draft, DateTimeOffset.UtcNow) { OutputDir = folder }, TestContext.Current.CancellationToken);
        return (request, folder);
    }

    private static string[] Asides(string folder) => Directory.GetFiles(Path.Combine(folder, "seq"), "*(changed*");

    private RunRecord Last(string jobId) => _repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    [Fact]
    public async Task A_cancel_after_a_rename_leaves_the_count_on_the_cancelled_row()
    {
        const string jobId = "job-1059-cancel";
        var gcp = new FakeGcp();
        var (request, folder) = await SeedEditedFirstFileAsync(jobId);
        using var cts = new CancellationTokenSource();
        var storage = new SecondFileHook(gcp, () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var runner = Runner(gcp, storage, _repo);

        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunAsync(request, cts.Token));
        Asides(folder).Length.ShouldBe(1, "vacuity: the first file was renamed aside before the cancel");
        await runner.CancelAsync(new CloudScope(Project), jobId, TestContext.Current.CancellationToken);

        Last(jobId).Phase.ShouldBe(JobPhase.Cancelled);
        KeptAsideDetail.TryRead(Last(jobId).ErrorDetail).ShouldBe(1, "the cancelled row must still say a changed file was kept under a new name");
    }

    [Fact]
    public async Task A_project_wide_cloud_error_after_a_rename_leaves_the_count_on_the_failed_row()
    {
        const string jobId = "job-1059-403";
        var gcp = new FakeGcp();
        var (request, folder) = await SeedEditedFirstFileAsync(jobId);
        var storage = new SecondFileHook(gcp, () => throw new CloudOperationException(new CloudError("403", 403, "forbidden"), CloudErrorKind.Permission));

        var result = await Runner(gcp, storage, _repo).RunAsync(request, TestContext.Current.CancellationToken);

        Asides(folder).Length.ShouldBe(1, "vacuity: the first file was renamed aside before the second failed");
        result.FinalPhase.ShouldBe(JobPhase.Failed);
        Last(jobId).Phase.ShouldBe(JobPhase.Failed);
        Last(jobId).ErrorCode.ShouldBe(RunErrorCodes.For(CloudErrorKind.Permission), "the primary code stays the headline");
        KeptAsideDetail.TryRead(Last(jobId).ErrorDetail).ShouldBe(1);
    }

    [Fact]
    public async Task A_raw_exception_after_a_rename_leaves_the_count_on_the_failed_row()
    {
        const string jobId = "job-1059-raw";
        var gcp = new FakeGcp();
        var (request, folder) = await SeedEditedFirstFileAsync(jobId);
        var storage = new SecondFileHook(gcp, () => throw new InvalidOperationException("boom"));

        var result = await Runner(gcp, storage, _repo).RunAsync(request, TestContext.Current.CancellationToken);

        Asides(folder).Length.ShouldBe(1, "vacuity");
        result.FinalPhase.ShouldBe(JobPhase.Failed);
        KeptAsideDetail.TryRead(Last(jobId).ErrorDetail).ShouldBe(1);
    }

    [Fact]
    public async Task A_resume_after_the_app_closed_mid_download_still_reports_the_file_the_first_attempt_renamed()
    {
        const string jobId = "job-1059-resume";
        var gcp = new FakeGcp();
        var (request, folder) = await SeedEditedFirstFileAsync(jobId);
        using var closing = new CancellationTokenSource();
        var storage = new SecondFileHook(gcp, () =>
        {
            // The app is closing: the first attempt stops mid-download and writes no terminal row.
            closing.Cancel();
            throw new OperationCanceledException(closing.Token);
        });
        await Should.ThrowAsync<OperationCanceledException>(() => Runner(gcp, storage, _repo).RunAsync(request, closing.Token));
        Asides(folder).Length.ShouldBe(1, "vacuity: attempt 1 renamed the edited file");
        JobStateMachine.IsTerminal(Last(jobId).Phase).ShouldBeFalse("vacuity: attempt 1 left no terminal row");

        // Attempt 2: the first file now matches the bucket byte for byte, so this attempt renames nothing itself.
        storage.OnSecondFile = null;
        var result = await Runner(gcp, storage, _repo).RunAsync(request, TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        Asides(folder).Length.ShouldBe(1, "the aside file is still there");
        KeptAsideDetail.TryRead(Last(jobId).ErrorDetail).ShouldBe(1, "the finished run still tells the user about the file attempt 1 set aside");
        Last(jobId).ErrorCode.ShouldBe(RunErrorCodes.ChangedFilesKeptAside);
    }
}
