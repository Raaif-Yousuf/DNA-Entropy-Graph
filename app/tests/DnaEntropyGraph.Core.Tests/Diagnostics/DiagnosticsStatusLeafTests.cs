using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Diagnostics;

/// <summary>
/// #590: which leaves of a worker <c>status.json</c> the support bundle copies. MEASURED 2026-10-03: the worker
/// (<c>worker/src/dna_entropy/worker/status.py</c>, <c>runner.py</c>) writes a user-derived string only into
/// <c>detail.input</c>, <c>detail.contig</c>, <c>detail.inputs</c>, <c>vm.name</c>, <c>error.message</c>,
/// <c>error.detail</c> and <c>error.remediation</c>; every leaf the bundle allowlists is a constant, a number, a
/// worker-generated id, a hardware string or an error code. This pins that: the shipped leaves are exactly the
/// expected set, so allowlisting a free-text leaf later turns this red and forces a decision.
/// </summary>
public sealed class DiagnosticsStatusLeafTests
{
    private const string Marker = "PLANTED-NAME-";

    // Every key the worker's StatusWriter state and error dict can carry, plus the contract's detail keys.
    private static readonly string[] ShippedLeaves =
    [
        "schema", "jobId", "stage", "percent", "startedAt", "updatedAt", "heartbeatSeq",
        "detail.window", "detail.windows", "detail.direction",
        "vm.zone", "vm.gpu", "vm.driver",
        "worker.version", "worker.image",
        "error.code", "error.retriable",
    ];

    private static readonly string[] DroppedFreeTextLeaves =
    [
        "detail.input", "detail.contig", "vm.name", "error.message", "error.detail", "error.remediation",
    ];

    private sealed class MemorySource(Dictionary<string, string> files) : IDiagnosticsSource
    {
        public IReadOnlyList<string> ListFiles() => files.Keys.ToList();

        public DiagnosticsFile? TryRead(string relativePath, long maxBytes) =>
            files.TryGetValue(relativePath, out var text) ? new DiagnosticsFile(Encoding.UTF8.GetBytes(text), false) : null;
    }

    private static readonly DiagnosticsInfo Info = new(
        "0.1.0", "Windows", ".NET 10", null, Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "jdoe"), [], DateTimeOffset.UnixEpoch, "readme");

    // A status.json whose every leaf, allowlisted or not, carries a marker naming its own path (numbers and the
    // retriable flag keep their types, as the worker writes them).
    private static string PlantedStatus()
    {
        var root = new JsonObject();
        foreach (var path in ShippedLeaves.Concat(DroppedFreeTextLeaves))
        {
            var parts = path.Split('.');
            var node = root;
            foreach (var part in parts[..^1])
            {
                node = (JsonObject)(node[part] ??= new JsonObject());
            }

            node[parts[^1]] = path switch
            {
                "schema" or "heartbeatSeq" or "detail.window" or "detail.windows" => JsonValue.Create(1),
                "percent" => JsonValue.Create(42.5),
                "error.retriable" => JsonValue.Create(false),
                _ => JsonValue.Create(Marker + path),
            };
        }

        return root.ToJsonString();
    }

    private static IEnumerable<string> LeafPaths(JsonNode? node, string prefix)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, child) in obj)
            {
                foreach (var leaf in LeafPaths(child, prefix.Length == 0 ? key : prefix + "." + key))
                {
                    yield return leaf;
                }
            }
        }
        else
        {
            yield return prefix;
        }
    }

    private static Dictionary<string, string> Build(RunRecord run)
    {
        var files = new Dictionary<string, string> { ["runs/job-1/status.json"] = PlantedStatus() };
        var zip = DiagnosticsBundleBuilder.Build(new MemorySource(files), [run], Info);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
    }

    // An imported run: the row exists, but it carries no manifest and no options, so nothing about the inputs was harvested from it.
    private static readonly RunRecord ImportedRun = new("job-1", JobPhase.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), Imported: true);

    [Fact]
    public void The_status_leaves_that_ship_are_exactly_the_enum_number_and_id_leaves()
    {
        var shipped = JsonNode.Parse(Build(ImportedRun)["files/runs/job-1/status.json"]);

        LeafPaths(shipped, string.Empty).ShouldBe(ShippedLeaves, ignoreOrder: true);
        ShippedLeaves.Length.ShouldBeGreaterThan(10, "vacuity: the allowlist is being compared against something");
    }

    [Fact]
    public void A_user_derived_name_in_a_free_text_status_leaf_never_ships_for_an_imported_run()
    {
        var entries = Build(ImportedRun);

        foreach (var path in DroppedFreeTextLeaves)
        {
            string.Concat(entries.Values).ShouldNotContain(Marker + path);
        }
    }
}
