using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Loads <c>tests/contract-fixtures/readers_parity.json</c> (issue #393): the records, notices and
/// error text the worker's <c>read_fasta</c>/<c>read_genbank</c> produce for fixed inputs, replayed
/// against <see cref="DnaEntropyGraph.Core.Inputs.FastaLite" /> and <see cref="DnaEntropyGraph.Core.Inputs.GenBankLite" />.
/// The worker's own <c>test_readers_parity.py</c> replays the same file, so a wording change on either
/// side fails a test until the fixture and both ports agree. The file is read-only here.
/// </summary>
public static class ReadersParityFixture
{
    private static readonly Lazy<FixtureFile> Loaded = new(() =>
        JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(ContractFixtures.Path("readers_parity.json")))
        ?? throw new InvalidOperationException("readers_parity.json is empty"));

    public static IEnumerable<object[]> FastaCases() => Loaded.Value.FastaCases.Select(c => new object[] { c });

    public static IEnumerable<object[]> GenBankCases() => Loaded.Value.GenBankCases.Select(c => new object[] { c });

    public sealed class FixtureFile
    {
        [JsonPropertyName("fasta_cases")]
        public List<ParityCase> FastaCases { get; set; } = [];

        [JsonPropertyName("genbank_cases")]
        public List<ParityCase> GenBankCases { get; set; } = [];
    }

    public sealed class ParityCase
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("python")]
        public Expected Python { get; set; } = new();

        /// <summary>Where GenBankLite's text deliberately differs from the worker's: its own expected value, with the reason.</summary>
        [JsonPropertyName("csharp_divergence")]
        public Divergence? CSharpDivergence { get; set; }

        public override string ToString() => Id; // xUnit theory display name
    }

    public sealed class Expected
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("records")]
        public List<ExpectedRecord> Records { get; set; } = [];

        [JsonPropertyName("notices")]
        public List<string> Notices { get; set; } = [];
    }

    public sealed class ExpectedRecord
    {
        [JsonPropertyName("header")]
        public string? Header { get; set; }

        [JsonPropertyName("seq")]
        public string Seq { get; set; } = "";

        [JsonPropertyName("gene_count")]
        public int GeneCount { get; set; }
    }

    public sealed class Divergence
    {
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("notices")]
        public List<string>? Notices { get; set; }
    }
}
