using System.IO.Compression;
using System.Text;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Diagnostics;

/// <summary>Issue #245: both generations of the cloud audit log ride in the support zip, redacted like every other copied file.</summary>
public class CloudAuditBundleTests
{
    private const string Email = "jdoe@lab.example.edu";

    private static readonly DiagnosticsInfo Info = new(
        "0.1.0", "Windows 11", ".NET 10", "130", Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "jdoe"), [Email], new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), "readme");

    private sealed class MemorySource(Dictionary<string, string> files) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => files.Keys.ToList();

        public DiagnosticsFile? TryRead(string relativePath, long maxBytes)
            => files.TryGetValue(relativePath, out var text) ? new DiagnosticsFile(Encoding.UTF8.GetBytes(text), false) : null;
    }

    private static Dictionary<string, string> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void Both_audit_generations_are_in_the_zip_under_their_own_names_and_are_redacted()
    {
        var current = "{\"operation\":\"Compute.CreateVm\",\"outcome\":\"ok\",\"note\":\"" + Email + "\"}\n";
        var previous = "{\"operation\":\"Compute.StopVm\",\"outcome\":\"ok\"}\n";
        var folder = new Dictionary<string, string>
        {
            ["logs/cloud-audit.jsonl"] = current,
            ["logs/cloud-audit.1.jsonl"] = previous,
            ["auth/token"] = "secret",
        };

        var entries = Unzip(DiagnosticsBundleBuilder.Build(new MemorySource(folder), [], Info));

        entries.ShouldContainKey("files/logs/cloud-audit.jsonl");
        entries.ShouldContainKey("files/logs/cloud-audit.1.jsonl");
        entries["files/logs/cloud-audit.jsonl"].ShouldContain("Compute.CreateVm");
        entries["files/logs/cloud-audit.jsonl"].ShouldNotContain(Email);
        entries["files/logs/cloud-audit.1.jsonl"].ShouldContain("Compute.StopVm");
        entries.Keys.ShouldNotContain(k => k.Contains("auth", StringComparison.Ordinal));
    }
}
