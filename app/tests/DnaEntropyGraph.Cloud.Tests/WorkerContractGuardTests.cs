using System.Text.Json;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Guards that tie app-side copies of worker facts to the worker's own files. Both read files above the test
/// binary through <see cref="FixturePaths"/>, so they need the DEFAULT artifacts path: under a private
/// <c>--artifacts-path</c> outside the repo they fail to find the repo, the same as <c>CloudErrorClassifierTests</c>.
/// </summary>
public class WorkerContractGuardTests
{
    private static string Read(params string[] relative) => File.ReadAllText(Path.Combine([FixturePaths.RepoRoot, .. relative]));

    private static IReadOnlyList<string> ContractCodes()
    {
        using var doc = JsonDocument.Parse(Read("docs", "contract", "error-codes.json"));
        return doc.RootElement.GetProperty("codes").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToList();
    }

    [Fact]
    public void Every_worker_error_code_in_the_contract_is_mapped_to_its_own_run_code_or_listed_as_generic_on_purpose()
    {
        var codes = ContractCodes();
        codes.Count.ShouldBeGreaterThanOrEqualTo(10, "the contract file lists at least the ten codes known today; a scanner that finds none proves nothing");

        var unmapped = codes
            .Where(c => RunErrorCodes.ForWorkerStatusCode(c) is null && !RunErrorCodes.WorkerStatusCodesWithoutDedicatedCopy.Contains(c))
            .ToList();

        unmapped.ShouldBeEmpty("a worker code with no run code and no 'generic on purpose' entry reads as a generic failure by accident: map it (docs/copy_catalog.md has the action) or list it");
    }

    [Fact]
    public void The_generic_on_purpose_list_holds_only_codes_that_are_in_the_contract_and_really_unmapped()
    {
        var codes = ContractCodes();

        RunErrorCodes.WorkerStatusCodesWithoutDedicatedCopy.ShouldNotBeEmpty();
        foreach (var code in RunErrorCodes.WorkerStatusCodesWithoutDedicatedCopy)
        {
            codes.ShouldContain(code, "a stale entry: the contract no longer has this code");
            RunErrorCodes.ForWorkerStatusCode(code).ShouldBeNull($"{code} is mapped now, so it must leave the generic list");
        }
    }

    [Fact]
    public void The_manifest_builders_FP8_models_are_exactly_the_models_the_worker_marks_fp8()
    {
        var python = Read("worker", "src", "dna_entropy", "predictors", "hardware.py");
        var entries = Regex.Matches(python, @"""(?<id>evo2_[a-z0-9_]+)""\s*:\s*ModelRequirement\(\s*precision=""(?<precision>[a-z0-9]+)""")
            .Select(m => (Id: m.Groups["id"].Value, Precision: m.Groups["precision"].Value))
            .ToList();
        entries.Count.ShouldBeGreaterThanOrEqualTo(5, "MODEL_REQUIREMENTS lists at least five models today; the parse found fewer");
        entries.ShouldContain(e => e.Precision == "fp8");
        entries.ShouldContain(e => e.Precision == "bf16");

        foreach (var (id, precision) in entries)
        {
            var manifest = WorkerManifestBuilder.Build(
                new RunOptions { ModelId = id, RunTarget = "Cloud" },
                "job-fp8",
                "install-1",
                "0.1.0",
                null,
                "bucket",
                [new StagedInput("C:/x/seq.gb", "seq.gb")],
                DateTimeOffset.UtcNow);
            using var doc = JsonDocument.Parse(manifest);
            doc.RootElement.GetProperty("predictor").GetProperty("precision").GetString().ShouldBe(precision, $"model {id}");
        }
    }
}
