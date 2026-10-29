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

    // Python keyword form: stage="x" and the job_status variable that feeds stage=job_status in runner.py.
    [GeneratedRegex(@"\b(?:stage|job_status)\s*=\s*""([a-z][a-z-]*)""")]
    private static partial Regex PythonKeywordStage();

    // Python dict form: "stage": "x".
    [GeneratedRegex(@"""stage""\s*:\s*""([a-z][a-z-]*)""")]
    private static partial Regex PythonDictStage();

    // startup.sh: the status() helper called as `status booting`, `status "done"`, `status failed '{...}'`.
    [GeneratedRegex(@"(?:^|[\s;'])status[ \t]+""?([a-z][a-z-]*)""?(?=[ \t]|$)", RegexOptions.Multiline)]
    private static partial Regex ShellStatusCall();

    /// <summary>Every stage literal in Python source text (keyword form, dict form, the job_status variable).</summary>
    internal static IReadOnlySet<string> PythonStageLiterals(string text) =>
        PythonKeywordStage().Matches(text).Concat(PythonDictStage().Matches(text)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every stage startup.sh writes through its status() helper (comment lines skipped).</summary>
    internal static IReadOnlySet<string> ShellStageLiterals(string text)
    {
        var code = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        return ShellStatusCall().Matches(code).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

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

    private static IReadOnlySet<string> WorkerSourceStages()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot, "worker", "src", "dna_entropy", "worker"), "*.py", SearchOption.AllDirectories);
        return files.SelectMany(f => PythonStageLiterals(File.ReadAllText(f))).ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> StartupShStages() =>
        ShellStageLiterals(File.ReadAllText(Path.Combine(RepoRoot, "worker", "vm", "startup.sh")));

    [Fact]
    public void Every_stage_literal_the_worker_writes_is_in_the_C_sharp_list()
    {
        var literals = WorkerSourceStages();

        // MEASURED 2026-10-29: queued (dict), model-loading / restoring-cache / running (keyword), done / failed / cancelled (job_status) = 7.
        literals.Count.ShouldBeGreaterThanOrEqualTo(7, "the scanner found almost no stage literals: it is looking in the wrong place");
        foreach (var literal in new[] { "queued", "model-loading", "restoring-cache", "running", "done", "failed", "cancelled" })
        {
            literals.ShouldContain(literal, $"the scanner no longer sees \"{literal}\" in worker source");
        }

        foreach (var literal in literals)
        {
            WorkerStages.All.ShouldContain(literal, $"the worker writes stage \"{literal}\" that WorkerStages does not list");
        }
    }

    [Fact]
    public void Every_stage_startup_sh_writes_is_in_the_C_sharp_list()
    {
        var literals = StartupShStages();

        // MEASURED 2026-10-29: running, done, failed, booting, installing.
        literals.Count.ShouldBeGreaterThanOrEqualTo(5);
        foreach (var literal in new[] { "booting", "installing", "running", "done", "failed" })
        {
            literals.ShouldContain(literal, $"the scanner no longer sees \"{literal}\" in startup.sh");
        }

        foreach (var literal in literals)
        {
            WorkerStages.All.ShouldContain(literal, $"startup.sh writes stage \"{literal}\" that WorkerStages does not list");
        }
    }

    [Theory]
    [InlineData("\"stage\": \"zzz-new\",")]
    [InlineData("status.update(stage=\"zzz-new\")")]
    [InlineData("job_status = \"zzz-new\"")]
    public void A_new_python_stage_literal_in_any_form_is_seen_and_would_fail_the_parity(string line)
    {
        var seen = PythonStageLiterals(line);

        seen.ShouldBe(["zzz-new"]);
        WorkerStages.All.ShouldNotContain("zzz-new");
    }

    [Fact]
    public void A_new_startup_sh_stage_is_seen()
    {
        ShellStageLiterals("# status commented\nstatus zzz-new\nstatus failed '{}'\n  status \"done\"\n").ShouldBe(["zzz-new", "failed", "done"], ignoreOrder: true);
    }

    /// <summary>Lines in the C# that mean "a worker stage", where a private copy of the string can drift from <see cref="WorkerStages"/>.</summary>
    [Theory]
    [InlineData("DnaEntropyGraph.Core", "Cloud/HeartbeatWatch.cs")]
    [InlineData("DnaEntropyGraph.Cloud", "FakeGcp.cs")]
    [InlineData("DnaEntropyGraph.Cloud", "FakeGcp.Control.cs")]
    public void The_C_sharp_stage_consumers_use_the_WorkerStages_constants_not_their_own_strings(string project, string file)
    {
        var path = Path.Combine(RepoPaths.AppRoot, "src", project, file.Replace('/', Path.DirectorySeparatorChar));
        var offenders = new List<string>();
        var names = string.Join('|', WorkerStages.All.Select(Regex.Escape));
        var literal = new Regex("\"(" + names + ")\"");
        foreach (var (line, index) in File.ReadAllLines(path).Select((l, i) => (l, i)))
        {
            var code = line.TrimStart();
            if (code.StartsWith("//") || code.StartsWith('*') || code.StartsWith("///"))
            {
                continue;
            }

            // result.json / input entries carry a per-input "status" (done/failed), which is a different vocabulary that shares two words.
            if (code.Contains("[\"status\"]", StringComparison.Ordinal) || !literal.IsMatch(line))
            {
                continue;
            }

            offenders.Add($"{file}:{index + 1}: {code}");
        }

        offenders.ShouldBeEmpty("use WorkerStages.<Name> instead of a private stage string:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void A_stage_missing_from_the_C_sharp_list_would_be_caught()
    {
        var contract = ContractStages();

        contract.Except(WorkerStages.All.Where(s => s != "idle")).ShouldBe(["idle"]);
    }
}
