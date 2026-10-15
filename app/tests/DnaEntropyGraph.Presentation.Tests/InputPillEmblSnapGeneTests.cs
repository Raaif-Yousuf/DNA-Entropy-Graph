using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #685: an EMBL or SnapGene file gets its own kind label and shows the genes it carries, like GenBank.</summary>
public sealed class InputPillEmblSnapGeneTests
{
    private sealed class Strings : IStringResourceProvider
    {
        public string GetString(string key) => key switch
        {
            "NewRunPillGenesMany" => "{0} genes",
            "NewRunPillRecordsOne" => "1 record",
            "NewRunPillBases" => "{0} bases",
            _ => key,
        };
    }

    [Theory]
    [InlineData(InputKind.Embl, "NewRunPillKind_Embl")]
    [InlineData(InputKind.SnapGene, "NewRunPillKind_SnapGene")]
    public void The_pill_names_the_format_and_shows_the_gene_count(InputKind kind, string key)
    {
        var pill = new InputPillItem("C:\\x\\a.file", isPasted: false, new Strings());
        var generation = pill.BeginChecking();

        pill.Apply(new InputValidationResult(null, kind, 1, 126, []) { GeneCount = 2 }, generation);

        pill.KindText.ShouldBe(key);
        pill.SummaryText.ShouldContain("2 genes");
        pill.Kind.ShouldBe(kind);
    }
}
