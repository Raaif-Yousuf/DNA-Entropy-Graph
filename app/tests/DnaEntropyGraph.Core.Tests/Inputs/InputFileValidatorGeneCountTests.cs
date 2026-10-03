using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>Issue #63: the per-file pill shows how many genes a GenBank file already carries.</summary>
public sealed class InputFileValidatorGeneCountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-genecount-" + Guid.NewGuid().ToString("N"));

    public InputFileValidatorGeneCountTests() => Directory.CreateDirectory(_dir);

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

    private const string TwoGeneGenBank =
        "LOCUS       TEST1                     24 bp    DNA     linear   UNK 01-JAN-1980\n"
        + "FEATURES             Location/Qualifiers\n"
        + "     gene            1..12\n"
        + "                     /gene=\"a\"\n"
        + "     gene            13..24\n"
        + "                     /gene=\"b\"\n"
        + "ORIGIN\n"
        + "        1 acgtacgtac gtacgtacgt acgt\n"
        + "//\n";

    [Fact]
    public void A_GenBank_file_reports_the_genes_it_carries()
    {
        var path = Path.Combine(_dir, "two.gb");
        File.WriteAllText(path, TwoGeneGenBank);

        var result = InputFileValidator.Validate(path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false);

        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
        result.GeneCount.ShouldBe(2);
    }

    [Fact]
    public void A_FASTA_file_carries_no_genes()
    {
        var path = Path.Combine(_dir, "x.fasta");
        File.WriteAllText(path, ">r\nACGTACGTACGT\n");

        InputFileValidator.Validate(path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false).GeneCount.ShouldBe(0);
    }
}
