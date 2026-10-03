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

    private static readonly DiagnosticsInfo Info = new("0.1.0", "Windows", ".NET 10", null, @"C:\Users\jdoe", [], DateTimeOffset.UnixEpoch);

    private sealed class EmptySource : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => [];

        public byte[]? TryRead(string relativePath) => null;
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
}
