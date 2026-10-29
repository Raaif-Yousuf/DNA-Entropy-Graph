using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #141: <see cref="WorkerStages"/> feeds the support bundle's vocabulary, so a worker stage the C# list lacks would be blanked
/// when a user tags a run with that word. The list must equal docs/job_contract.md's "Stage list" and cover every stage the worker writes.
/// </summary>
public sealed partial class WorkerStagesParityTests
{
    private static string RepoRoot => Path.GetDirectoryName(RepoPaths.AppRoot)!;

    [GeneratedRegex(@"stage\s*=\s*""([a-z][a-z-]*)""")]
    private static partial Regex StageLiteral();

    private static IReadOnlySet<string> ContractStages()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "docs", "job_contract.md"));
        var start = text.IndexOf("### Stage list, in order", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, "the contract's stage list heading moved");
        var open = text.IndexOf("```", start, StringComparison.Ordinal);
        var close = text.IndexOf("```", open + 3, StringComparison.Ordinal);
        var block = Regex.Replace(text[(open + 3)..close], @"\([^)]*\)", " ");
        return Regex.Matches(block, @"[a-z]+(?:-[a-z]+)*").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void The_C_sharp_stage_list_equals_the_contract_stage_list()
    {
        var contract = ContractStages();

        contract.Count.ShouldBeGreaterThanOrEqualTo(13);
        WorkerStages.All.OrderBy(s => s, StringComparer.Ordinal).ShouldBe(contract.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_stage_literal_the_worker_writes_is_in_the_C_sharp_list()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot, "worker", "src", "dna_entropy", "worker"), "*.py", SearchOption.AllDirectories);
        var literals = files.SelectMany(f => StageLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

        literals.Count.ShouldBeGreaterThanOrEqualTo(3, "the scanner found almost no stage literals: it is looking in the wrong place");
        foreach (var literal in literals)
        {
            WorkerStages.All.ShouldContain(literal, $"the worker writes stage \"{literal}\" that WorkerStages does not list");
        }
    }

    [Fact]
    public void A_stage_missing_from_the_C_sharp_list_would_be_caught()
    {
        var contract = ContractStages();

        contract.Except(WorkerStages.All.Where(s => s != "idle")).ShouldBe(["idle"]);
    }
}
