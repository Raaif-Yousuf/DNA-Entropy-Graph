using DnaEntropyGraph.Core.Cost;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cost;

/// <summary>Issue #98: the price list that ships with the app (<c>Assets/pricing.json</c>) and what a bad one does.</summary>
public sealed class PricingTableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-pricing-" + Guid.NewGuid().ToString("N"));

    public PricingTableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    internal const string Good = """
        {
          "schema": 1,
          "asOf": "2026-09-19",
          "currency": "USD",
          "region": "us-central1",
          "source": "test source note",
          "disk": { "type": "pd-balanced", "usdPerGbMonth": 0.10 },
          "machines": {
            "g2-standard-8": { "accelerator": "1x NVIDIA L4", "onDemandUsdPerHour": 0.85, "spotUsdPerHour": 0.18 },
            "a2-highgpu-1g": { "accelerator": "1x NVIDIA A100 40GB", "onDemandUsdPerHour": 3.67, "spotUsdPerHour": null }
          }
        }
        """;

    [Fact]
    public void A_good_file_parses_into_a_table_with_every_field()
    {
        var result = PricingTable.Parse(Good);

        result.Problem.ShouldBeNull();
        var table = result.Table.ShouldNotBeNull();
        table.AsOf.ShouldBe(new DateOnly(2026, 9, 19));
        table.Region.ShouldBe("us-central1");
        table.Source.ShouldBe("test source note");
        table.DiskUsdPerHour(150).ShouldBe(150 * 0.10 / 730, 1e-12, "the run's own boot disk size is what is priced; the file carries no size");
        table.DiskUsdPerDay(300).ShouldBe(300 * 0.10 / 730 * 24, 1e-12);
        table.DiskUsdPerGbMonth.ShouldBe(0.10);
        table.Find("g2-standard-8")!.OnDemandUsdPerHour.ShouldBe(0.85);
        table.Find("g2-standard-8")!.SpotUsdPerHour.ShouldBe(0.18);
        table.Find("a2-highgpu-1g")!.SpotUsdPerHour.ShouldBeNull("a null spot price means unknown, never zero");
        table.Find("no-such-machine").ShouldBeNull();
    }

    public static TheoryData<string, string, PricingProblem> BadFiles() => new()
    {
        { "not json", "{ nope", PricingProblem.Malformed },
        { "empty", "", PricingProblem.Malformed },
        { "an array", "[]", PricingProblem.Malformed },
        { "a newer schema", Good.Replace("\"schema\": 1", "\"schema\": 2"), PricingProblem.UnsupportedSchema },
        { "no schema", Good.Replace("\"schema\": 1,", ""), PricingProblem.UnsupportedSchema },
        { "a date that is not a date", Good.Replace("2026-09-19", "yesterday"), PricingProblem.InvalidValue },
        { "no source note", Good.Replace("\"source\": \"test source note\",", ""), PricingProblem.InvalidValue },
        { "a currency that is not USD", Good.Replace("\"USD\"", "\"EUR\""), PricingProblem.InvalidValue },
        { "a zero on-demand price", Good.Replace("\"onDemandUsdPerHour\": 0.85", "\"onDemandUsdPerHour\": 0"), PricingProblem.InvalidValue },
        { "a negative spot price", Good.Replace("\"spotUsdPerHour\": 0.18", "\"spotUsdPerHour\": -1"), PricingProblem.InvalidValue },
        { "a spot price above on-demand", Good.Replace("\"spotUsdPerHour\": 0.18", "\"spotUsdPerHour\": 0.9"), PricingProblem.InvalidValue },
        { "a price given as text", Good.Replace("\"onDemandUsdPerHour\": 0.85", "\"onDemandUsdPerHour\": \"0.85\""), PricingProblem.InvalidValue },
        { "no machines", Good.Replace("\"machines\"", "\"other\""), PricingProblem.InvalidValue },
        { "an empty machine list", """{"schema":1,"asOf":"2026-09-19","currency":"USD","region":"r","source":"s","disk":{"usdPerGbMonth":0.1},"machines":{}}""", PricingProblem.InvalidValue },
        { "a zero disk price", Good.Replace("\"usdPerGbMonth\": 0.10", "\"usdPerGbMonth\": 0"), PricingProblem.InvalidValue },
        { "no disk", Good.Replace("\"disk\"", "\"dsk\""), PricingProblem.InvalidValue },
    };

    [Fact]
    public void The_bad_file_table_is_not_vacuous() => BadFiles().Count.ShouldBeGreaterThan(10);

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void A_bad_file_is_a_named_problem_with_no_table_and_never_an_exception(string name, string json, PricingProblem expected)
    {
        var result = PricingTable.Parse(json);

        result.Table.ShouldBeNull(name);
        result.Problem.ShouldBe(expected, name);
        result.Detail.ShouldNotBeNullOrWhiteSpace(name);
    }

    [Fact]
    public void The_file_source_reads_the_file_it_is_pointed_at()
    {
        var path = Path.Combine(_dir, "pricing.json");
        File.WriteAllText(path, Good);

        new FilePricingSource(path).Load().Table.ShouldNotBeNull();
    }

    [Fact]
    public void A_missing_file_is_FileMissing_not_a_crash()
        => new FilePricingSource(Path.Combine(_dir, "nope.json")).Load().Problem.ShouldBe(PricingProblem.FileMissing);

    [Fact]
    public void An_unreadable_file_is_Unreadable_not_a_crash()
    {
        // A directory where the file should be: opening it fails with an IO or access error.
        var path = Path.Combine(_dir, "isdir.json");
        Directory.CreateDirectory(path);

        new FilePricingSource(path).Load().Problem.ShouldBe(PricingProblem.Unreadable);
    }
}
