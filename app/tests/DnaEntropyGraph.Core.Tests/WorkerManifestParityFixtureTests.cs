using System.Text.Json;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.Tests.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #476: the app-built manifest and the worker's schema cannot drift apart. This side owns the
/// golden files under <c>tests/contract-fixtures/app_manifests/</c>: for every RunOptions combination
/// that changes the wire shape it asserts <see cref="WorkerManifestBuilder"/> still emits exactly the
/// committed file. The Python side (<c>worker/tests/test_app_manifest_parity.py</c> and
/// <c>scripts/gen_manifest_schema.py --check</c>) validates the same files against
/// <c>manifest.schema.json</c> and parses them with the worker's <c>JobManifest.parse</c>.
/// A builder change that moves the output fails here until the fixtures are regenerated; a fixture the
/// schema or parser refuses fails there. Regenerate with <c>DEG_REGEN_APP_MANIFESTS=1 dotnet test --filter WorkerManifestParityFixtureTests</c>.
/// </summary>
public class WorkerManifestParityFixtureTests
{
    private const string RegenVariable = "DEG_REGEN_APP_MANIFESTS";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<StagedInput> OneInput = [new StagedInput(@"C:\runs\j1\input\SetTnpB.gb", "SetTnpB.gb")];

    private static RunOptions Base() => new() { ModelId = "evo2_7b", RunTarget = "Cloud" };

    private static string FixtureDir => ContractFixtures.Path("app_manifests");

    private static string Emit(RunOptions options, IReadOnlyList<StagedInput>? inputs = null, string? localRoot = null)
        => WorkerManifestBuilder.Build(options, "20261002-120000-abcdef", "install-1", "0.1.0", "reg/x@sha256:a", "deg-123-abc", inputs ?? OneInput, Now, localRoot);

    /// <summary>Every case: a file name stem and the manifest the builder writes for it.</summary>
    public static IEnumerable<(string Name, string Json)> Cases()
    {
        yield return ("default_cloud", Emit(Base()));

        foreach (var after in Enum.GetValues<AfterTaskAction>())
        {
            foreach (var then in Enum.GetValues<AfterKeepAliveAction>())
            {
                var o = Base() with { AfterTask = after, KeepAliveMinutes = 45, AfterKeepAlive = then };
                yield return ($"lifecycle_{after}_{then}".ToLowerInvariant(), Emit(o));
            }
        }

        foreach (var topology in Enum.GetValues<Topology>())
        {
            yield return ($"topology_{topology}".ToLowerInvariant(), Emit(Base() with { Topology = topology }));
        }

        foreach (var direction in Enum.GetValues<Direction>())
        {
            yield return ($"direction_{direction}".ToLowerInvariant(), Emit(Base() with { Direction = direction }));
        }

        yield return ("track_wig", Emit(Base() with { TrackFormat = TrackFormat.Wig, OutputFiles = OutputCatalog.Defaults | OutputFileKinds.Wig }));
        yield return ("output_gc", Emit(Base() with { OutputFiles = OutputCatalog.Defaults | OutputFileKinds.Gc, GcWindow = 101 }));
        yield return ("model_fp8", Emit(Base() with { ModelId = "evo2_40b" }));
        yield return ("predictor_mock", Emit(Base() with { Predictor = PredictorSelection.MockTestPredictor, Seed = 7 }));
        yield return ("input_options", Emit(Base() with
        {
            Format = InputFormat.Plain,
            TreatAsRna = true,
            StartCoordinate = 5,
            FindGenes = false,
            AmbiguityPolicy = AmbiguityPolicy.Mask,
            FastaRecords = FastaRecordsSelection.FirstOnly,
        }));
        yield return ("input_annotation", Emit(Base(), [new StagedInput("x", "a.fasta"), new StagedInput("y", "genes.gff3", IsAnnotation: true)]));
        yield return ("input_accession_range", Emit(Base(), [StagedInput.ForAccession("NC_045512.2", "100-500")]));
        yield return ("input_two_files", Emit(Base(), [new StagedInput("x", "a.gb"), new StagedInput("y", "b.fasta")]));
        yield return ("store_localdir", Emit(Base() with { RunTarget = "This PC" }, localRoot: @"C:\Users\lab\AppData\Local\DNAEntropyGraph\local-store"));
    }

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases())
        {
            data.Add(c.Name);
        }

        return data;
    }

    private static string Pretty(string json)
        => JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_builder_emits_exactly_the_committed_fixture(string name)
    {
        var json = Cases().Single(c => c.Name == name).Json;
        var path = Path.Combine(FixtureDir, name + ".json");

        if (Environment.GetEnvironmentVariable(RegenVariable) == "1")
        {
            Directory.CreateDirectory(FixtureDir);
            File.WriteAllText(path, Pretty(json), new System.Text.UTF8Encoding(false));
        }

        File.Exists(path).ShouldBeTrue($"{path} is missing: regenerate with {RegenVariable}=1");
        var committed = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode.DeepEquals(JsonNode.Parse(json), committed).ShouldBeTrue($"{name}.json is stale against WorkerManifestBuilder: regenerate with {RegenVariable}=1");
    }

    [Fact]
    public void No_fixture_is_left_without_a_case()
    {
        var names = Cases().Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var orphans = Directory.Exists(FixtureDir)
            ? Directory.GetFiles(FixtureDir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => !names.Contains(n!)).ToList()
            : [];
        orphans.ShouldBeEmpty("delete fixtures no case emits any more");
    }

    [Fact]
    public void Keep_alive_reaches_the_wire_with_the_ui_value_and_schema_is_an_integer()
    {
        var m = JsonNode.Parse(Cases().Single(c => c.Name == "lifecycle_keepalive_delete").Json)!;

        m["lifecycle"]!["afterTask"]!.GetValue<string>().ShouldBe("keep");
        m["lifecycle"]!["keepAliveMinutes"]!.GetValue<int>().ShouldBe(45);
        m["lifecycle"]!["afterKeepAlive"]!.GetValue<string>().ShouldBe("delete");
        m["schema"]!.GetValueKind().ShouldBe(JsonValueKind.Number);
        m["schema"]!.ToJsonString().ShouldBe("1");
    }
}
