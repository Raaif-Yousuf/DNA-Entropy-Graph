using System.IO.Compression;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Diagnostics;

public class DiagnosticsExporterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deg-diag-" + Guid.NewGuid().ToString("n"));

    public DiagnosticsExporterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly DiagnosticsInfo Info = new("0.1.0", "Windows", ".NET 10", null, Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "jdoe"), [], DateTimeOffset.UnixEpoch, "readme");

    private sealed class EmptySource : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => [];

        public DiagnosticsFile? TryRead(string relativePath, long maxBytes) => null;
    }

    private static IRunRepository History(params RunRecord[] runs)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(runs);
        return repository;
    }

    [Fact]
    public async Task Writes_a_readable_zip_at_the_chosen_path_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_folder, "out.zip");
        var run = new RunRecord("job-1", JobPhase.Failed, DateTimeOffset.UnixEpoch, ErrorCode: "WORKER_FAILED");

        await new DiagnosticsExporter(new EmptySource(), History(run), () => Info).ExportAsync(path, CancellationToken.None);

        using var archive = ZipFile.OpenRead(path);
        archive.Entries.Select(e => e.FullName).ShouldContain("run-history.json");
        Directory.GetFiles(_folder).ShouldBe([path]);
    }

    [Fact]
    public async Task A_refused_bundle_writes_nothing_at_all()
    {
        var path = Path.Combine(_folder, "out.zip");
        var leaky = new RunRecord("ACGTTGCAAGGCTTAACCGGTTAAGGCC", JobPhase.Failed, DateTimeOffset.UnixEpoch);

        await Should.ThrowAsync<DiagnosticsLeakException>(
            () => new DiagnosticsExporter(new EmptySource(), History(leaky), () => Info).ExportAsync(path, CancellationToken.None));

        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_bundle_removes_the_empty_file_the_save_picker_made()
    {
        var path = Path.Combine(_folder, "out.zip");
        await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
        var leaky = new RunRecord("ACGTTGCAAGGCTTAACCGGTTAAGGCC", JobPhase.Failed, DateTimeOffset.UnixEpoch);

        await Should.ThrowAsync<DiagnosticsLeakException>(
            () => new DiagnosticsExporter(new EmptySource(), History(leaky), () => Info).ExportAsync(path, CancellationToken.None));

        File.Exists(path).ShouldBeFalse();
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_history_read_removes_the_empty_file_too()
    {
        var path = Path.Combine(_folder, "out.zip");
        await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<RunRecord>>(new IOException("db locked")));

        await Should.ThrowAsync<IOException>(
            () => new DiagnosticsExporter(new EmptySource(), repository, () => Info).ExportAsync(path, CancellationToken.None));

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_cancelled_before_the_export_starts_still_removes_the_empty_file_the_picker_made()
    {
        var path = Path.Combine(_folder, "out.zip");
        await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => new DiagnosticsExporter(new EmptySource(), History(), () => Info).ExportAsync(path, cancelled.Token));

        File.Exists(path).ShouldBeFalse();
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_bundle_leaves_an_existing_file_with_content_exactly_as_it_was()
    {
        var path = Path.Combine(_folder, "out.zip");
        await File.WriteAllTextAsync(path, "earlier bundle", TestContext.Current.CancellationToken);
        var leaky = new RunRecord("ACGTTGCAAGGCTTAACCGGTTAAGGCC", JobPhase.Failed, DateTimeOffset.UnixEpoch);

        await Should.ThrowAsync<DiagnosticsLeakException>(
            () => new DiagnosticsExporter(new EmptySource(), History(leaky), () => Info).ExportAsync(path, CancellationToken.None));

        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("earlier bundle");
    }
}
