using System.Text.Json;
using DnaEntropyGraph.Core.Cloud.Audit;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cloud;

/// <summary>Issue #245: the audit file is append-only JSON lines, size-bounded, and never throws.</summary>
public class JsonLinesCloudAuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static CloudAuditEntry Entry(string op = "Compute.CreateVm") => new(
        new DateTimeOffset(2026, 10, 16, 1, 2, 3, TimeSpan.Zero), "inst", "job-1", op, "zones/z/instances/deg-job-1", "req", "op-1", "ok", null, 12);

    [Fact]
    public void Each_entry_is_one_json_line_and_lines_append()
    {
        var path = Path.Combine(_dir, "logs", "cloud-audit.jsonl");
        var log = new JsonLinesCloudAuditLog(path);

        log.Record(Entry("Compute.CreateVm"));
        log.Record(Entry("Compute.StopVm"));

        var lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        File.ReadAllText(path).ShouldNotContain("\r");
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("operation").GetString().ShouldBe("Compute.CreateVm");
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("outcome").GetString().ShouldBe("ok");
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("timeUtc").GetString().ShouldStartWith("2026-10-16T01:02:03");
    }

    [Fact]
    public void The_file_rotates_at_the_limit_and_keeps_one_previous_generation()
    {
        var path = Path.Combine(_dir, "cloud-audit.jsonl");
        var log = new JsonLinesCloudAuditLog(path, maxBytes: 600);

        for (var i = 0; i < 40; i++)
        {
            log.Record(Entry("Op" + i));
        }

        File.Exists(log.RotatedPath).ShouldBeTrue();
        Path.GetFileName(log.RotatedPath).ShouldBe("cloud-audit.1.jsonl");
        new FileInfo(path).Length.ShouldBeLessThan(1200);
        new FileInfo(log.RotatedPath).Length.ShouldBeLessThan(1200);
        File.ReadAllText(path).ShouldContain("Op39");
    }

    [Fact]
    public void An_unusable_path_loses_the_line_and_never_throws()
    {
        Directory.CreateDirectory(_dir);
        // The path is an existing directory: opening it for append fails.
        var log = new JsonLinesCloudAuditLog(_dir);

        Should.NotThrow(() => log.Record(Entry()));
        Should.NotThrow(() => new JsonLinesCloudAuditLog("\0bad").Record(Entry()));
    }
}
