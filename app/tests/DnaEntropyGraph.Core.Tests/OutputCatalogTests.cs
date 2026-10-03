using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #103: the one table of the output files a run can ask the worker for, and what it must agree with.</summary>
public sealed partial class OutputCatalogTests
{
    /// <summary>The worker's vocabulary as job_contract.md section 3 states it (the <c>outputs</c> row's "a | b | c" list).</summary>
    private static List<string> ContractVocabulary()
    {
        var doc = File.ReadAllText(Path.Combine(Inputs.ContractFixtures.RepoRoot, "docs", "job_contract.md"));
        var row = doc.Split('\n').Single(line => line.StartsWith("| `outputs` | Array naming every desired output file", StringComparison.Ordinal));
        var list = VocabularyList().Match(row).Groups["names"].Value;
        return list.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim('`', '\\', ' '))
            .Where(n => n.Length > 0)
            .ToList();
    }

    [GeneratedRegex(@"individually: `(?<names>[a-z0-9_]+(?: \\\| [a-z0-9_]+)+)` \(see")]
    private static partial Regex VocabularyList();

    [Fact]
    public void The_catalog_names_exactly_the_files_the_worker_contract_lists_in_the_same_order()
    {
        var vocabulary = ContractVocabulary();

        vocabulary.Count.ShouldBeGreaterThan(10, "vacuity: the contract row was really parsed");
        OutputCatalog.Entries.Select(e => e.WireName).ToList().ShouldBe(vocabulary);
    }

    [Fact]
    public void Every_flag_has_exactly_one_entry_and_every_entry_one_flag()
    {
        var kinds = OutputCatalog.Entries.Select(e => e.Kind).ToList();

        kinds.Distinct().Count().ShouldBe(kinds.Count);
        kinds.ShouldAllBe(k => k != OutputFileKinds.None && (((int)k) & ((int)k - 1)) == 0, "one bit each");
        kinds.Aggregate(OutputFileKinds.None, (a, k) => a | k).ShouldBe(OutputCatalog.Everything);
        OutputCatalog.Entries.Select(e => e.LabelKey).Distinct().Count().ShouldBe(OutputCatalog.Entries.Count);
    }

    [Fact]
    public void Only_wig_and_the_probability_matrix_are_off_by_default()
    {
        OutputCatalog.Entries.Where(e => !e.DefaultOn).Select(e => e.WireName).ShouldBe(["wig", "probs", "probs_npy"], "WIG is the alternative to the bedGraph track, not an addition: the worker writes one track format per run");
        OutputCatalog.Defaults.ShouldBe(OutputCatalog.Everything & ~(OutputFileKinds.Wig | OutputFileKinds.Probs | OutputFileKinds.ProbsNpy));
        new RunOptions { ModelId = "m", RunTarget = "Cloud" }.OutputFiles.ShouldBe(OutputCatalog.Defaults);
    }

    [Fact]
    public void Every_label_key_is_a_string_in_Resources_resw_with_no_em_dash()
    {
        var resw = File.ReadAllText(Path.Combine(Inputs.ContractFixtures.RepoRoot, "app", "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw"));

        foreach (var entry in OutputCatalog.Entries)
        {
            var match = Regex.Match(resw, "<data name=\"" + Regex.Escape(entry.LabelKey) + "\"[^>]*>\\s*<value>(?<text>[^<]*)</value>");
            match.Success.ShouldBeTrue($"{entry.LabelKey} is missing from Resources.resw");
            match.Groups["text"].Value.ShouldNotContain("—", Case.Sensitive, entry.LabelKey);
            match.Groups["text"].Value.Length.ShouldBeGreaterThan(10, entry.LabelKey);
        }
    }

    [Fact]
    public void Gene_files_need_genes_and_nothing_else_does()
    {
        OutputCatalog.Entries.Where(e => e.NeedsGenes).Select(e => e.WireName).ShouldBe(["genes_gff3", "gene_summary"]);
    }

    [Theory]
    [InlineData(InputKind.GenBank, OutputInputKinds.GenBank)]
    [InlineData(InputKind.Fasta, OutputInputKinds.Sequence)]
    [InlineData(InputKind.Paste, OutputInputKinds.Sequence)]
    public void An_input_kind_maps_to_its_output_group(InputKind kind, OutputInputKinds group)
        => OutputCatalog.GroupFor(kind).ShouldBe(group);

    [Theory]
    [InlineData(OutputInputKinds.GenBank)]
    [InlineData(OutputInputKinds.Sequence)]
    public void Each_group_is_offered_every_file_the_worker_can_write_for_it(OutputInputKinds group)
    {
        // pipeline.py gates every writer on its own include_* flag for the GenBank path and the FASTA/paste path alike.
        OutputCatalog.For(group).Select(e => e.WireName).ShouldBe(OutputCatalog.Entries.Select(e => e.WireName));
    }

    [Fact]
    public void Wire_names_round_trip_and_unknown_names_are_dropped()
    {
        var kinds = OutputFileKinds.Stats | OutputFileKinds.Wig | OutputFileKinds.Probs;

        OutputCatalog.ToWireNames(kinds).ShouldBe(["wig", "stats", "probs"]);
        OutputCatalog.FromWireNames(["probs", "wig", "stats", "no_such_file"]).ShouldBe(kinds);
        OutputCatalog.FromWireNames([]).ShouldBe(OutputFileKinds.None);
    }

    [Fact]
    public void The_legacy_All_still_means_the_eight_files_a_stored_row_was_written_with()
    {
        // A stored OptionsJson says "All" by name; adding flags must not silently widen what that row meant.
        OutputFileKinds.All.ShouldBe(
            OutputFileKinds.GenBank | OutputFileKinds.Fasta | OutputFileKinds.BedGraph | OutputFileKinds.Wig
            | OutputFileKinds.GeneiousGff3 | OutputFileKinds.GenesGff3 | OutputFileKinds.Stats | OutputFileKinds.EntropyTsv);
    }

    [Fact]
    public void Both_track_formats_ticked_normalizes_to_the_bedgraph_because_the_worker_writes_one()
    {
        OutputCatalog.Normalize(OutputFileKinds.BedGraph | OutputFileKinds.Wig | OutputFileKinds.Stats).ShouldBe(OutputFileKinds.BedGraph | OutputFileKinds.Stats);
        OutputCatalog.Normalize(OutputFileKinds.Wig | OutputFileKinds.Stats).ShouldBe(OutputFileKinds.Wig | OutputFileKinds.Stats);
    }
}
